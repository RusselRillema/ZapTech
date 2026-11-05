using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace PaymentIntegration.Demo;

// HTTP orchestration and observations only. The service owns payment rules and persistence.
internal sealed class DemoRunner(HttpClient payments, HttpClient acme, DemoSettings settings, TextWriter output)
{
    public static readonly string[] Scenarios =
        ["approval", "decline", "duplicate-submission", "duplicate-notification", "out-of-order", "timeout"];
    public static readonly string[] AdjustmentScenarios =
        ["reversal", "refund", "reversal-before-approval", "refund-before-approval", "refund-then-reversal", "reversal-then-refund"];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions DisplayJson = new(Json) { WriteIndented = true };
    private readonly Stopwatch elapsed = new();
    private readonly TextWriter log = TextWriter.Synchronized(output);

    public async Task RunAsync(string name, CancellationToken cancellationToken)
    {
        elapsed.Restart();
        Log("Demo", $"Scenario {name}: {DemoConsole.Describe(name)}");
        Log("Demo", "A = original payment request; B = Acme controls/callbacks; Observe = stored state checks. These are async flows, not fixed threads.");
        Log("Demo", "Checking PaymentIntegration and Acme health and reading the current stub configuration...");
        await HealthAsync(payments, "PaymentIntegration", cancellationToken);
        await HealthAsync(acme, "Acme", cancellationToken);
        var previous = await GetAsync<Scenario>(acme, "/__stub/scenario", cancellationToken)
            ?? throw new InvalidOperationException("Enable ACME_ENABLE_CONTROLS in the dedicated demo stub.");
        var request = new DemoPaymentRequest($"demo-{name}-{Guid.NewGuid():N}", settings.CustomerId,
            settings.CustomerCardId, settings.SubMerchantId, settings.Amount, settings.CurrencyCode);
        Log("Demo", $"appPaymentId={request.AppPaymentId}; amounts are in {request.CurrencyCode} minor units.");
        try
        {
            var delay = name == "timeout" ? 40 : name == "out-of-order" || name.EndsWith("-before-approval", StringComparison.Ordinal) ? 15 : 0;
            await ConfigureAsync(new(name == "decline" ? "Declined" : "Approved", delay), cancellationToken);
            if (AdjustmentScenarios.Contains(name, StringComparer.Ordinal))
            {
                await AdjustmentsAsync(name, request, cancellationToken);
                return;
            }
            if (name is "out-of-order" or "timeout")
            {
                await DelayedAsync(name, request, cancellationToken);
                return;
            }

            var submitted = await SubmitAsync(request, cancellationToken);
            if (name == "decline")
                ExpectDeclined(submitted, request);
            else
                ExpectApproved(submitted, request);

            if (name == "duplicate-submission")
            {
                Log("A", "Submitting the same appPaymentId and details again to check idempotency.");
                var repeated = await SubmitAsync(request, cancellationToken);
                Require(repeated == submitted, "Repeating the same app payment changed its stored result.");
                var transaction = await TransactionAsync(submitted.PaymentId, cancellationToken);
                Require(transaction.SubmissionCount == 1, "The service submitted the payment to Acme more than once.");
                Log("Observe", $"Acme observed submissions={transaction.SubmissionCount}; same paymentId={repeated.PaymentId}");
            }

            await SendAdviceAsync(submitted.PaymentId, cancellationToken);
            var before = await ReconciledAsync(request.AppPaymentId, submitted.State, cancellationToken);
            if (name == "duplicate-notification")
            {
                Log("B", "Replaying the same Payment Advice to check duplicate delivery handling.");
                await SendAdviceAsync(submitted.PaymentId, cancellationToken);
                var after = await ReconciledAsync(request.AppPaymentId, submitted.State, cancellationToken);
                Require(after == before, "Replaying Payment Advice changed the payment result.");
            }
            Observe("Stored payment", before);
        }
        finally
        {
            // Restore the prior configuration even when the scenario deadline/caller is cancelled.
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            Log("Demo", "Restoring the previous Acme scenario configuration...");
            await ConfigureAsync(previous, cleanup.Token);
        }
    }

    private async Task DelayedAsync(string name, DemoPaymentRequest request, CancellationToken cancellationToken)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var submission = SubmitAsync(request, pending.Token);
        try
        {
            Log("Observe", "Looking for the persisted payment while A waits for the delayed Acme response...");
            var initial = await WaitAsync(() => FindAsync(request.AppPaymentId, cancellationToken),
                _ => true, "payment to be persisted", cancellationToken);
            ExpectUnresolved(initial, request);
            Observe("During delayed response", initial);
            await AdviceAsync(initial.PaymentId, cancellationToken);

            if (name == "out-of-order")
            {
                Log("B", "Delivering held approval Advice while A is still waiting. Advice does not supply authoritative AuthAmount.");
                await SendAdviceAsync(initial.PaymentId, cancellationToken);
                Require(!submission.IsCompleted, "The original reply arrived before the out-of-order advice was observed.");
                var afterAdvice = await FindAsync(request.AppPaymentId, cancellationToken)
                    ?? throw new InvalidOperationException("The persisted payment disappeared.");
                ExpectUnresolved(afterAdvice, request);
                Observe("Advice acknowledged before authoritative AuthAmount", afterAdvice);
                Log("A", "Still waiting for Acme's delayed response with authoritative AuthAmount...");
            }
            else
            {
                Log("A", "Waiting for PaymentIntegration to time out its Acme call and attempt RTTQ recovery. Advice remains held.");
            }

            var result = await submission;
            ExpectApproved(result, request);
            if (name == "timeout")
            {
                var transaction = await WaitAsync(() => FindTransactionAsync(initial.PaymentId, cancellationToken),
                    item => item.ResponseInterrupted, "Acme to observe the interrupted payment response", cancellationToken);
                Require(transaction.SubmissionCount == 1 && transaction.ResponseDelaySeconds == 40,
                    "The timeout scenario did not retain a single delayed Acme submission.");
                Log("Observe", "Acme observed an interrupted reply; the service recovered approval before any advice was released.");
                await SendAdviceAsync(initial.PaymentId, cancellationToken);
            }
            var stored = await ReconciledAsync(request.AppPaymentId, "Approved", cancellationToken);
            ExpectApproved(stored, request);
            Observe("Reconciled payment", stored);
        }
        finally
        {
            pending.Cancel();
            // Observe the outstanding HTTP task if another check failed before awaiting it.
            try { await submission; }
            catch (Exception) when (submission.IsCanceled || submission.IsFaulted) { }
        }
    }

    private async Task ConfigureAsync(Scenario scenario, CancellationToken cancellationToken)
    {
        Log("B", $"Configuring Acme: outcome={scenario.PaymentOutcome}, response delay={scenario.ResponseDelaySeconds}s, hold callbacks={scenario.HoldCallbacks}.");
        using var response = await acme.PostAsJsonAsync("/__stub/scenario", scenario, cancellationToken);
        await CheckAsync(response, "Configure Acme scenario", cancellationToken);
        Log("B", $"Acme configuration accepted (HTTP {(int)response.StatusCode}).");
    }

    private async Task AdjustmentsAsync(string name, DemoPaymentRequest request, CancellationToken cancellationToken)
    {
        var early = name.EndsWith("-before-approval", StringComparison.Ordinal);
        var firstType = name.StartsWith("refund", StringComparison.Ordinal) ? "Refund" : "Reversal";
        var firstState = firstType == "Refund" ? "Refunded" : "Reversed";
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var submission = SubmitAsync(request, pending.Token);
        try
        {
            ObservedPayment payment;
            if (early)
            {
                Log("Observe", "Looking for the persisted payment while A waits, so B can deliver an adjustment first...");
                payment = await WaitAsync(() => FindAsync(request.AppPaymentId, cancellationToken),
                    _ => true, "payment to be persisted", cancellationToken);
                ExpectUnresolved(payment, request);
                await AdviceAsync(payment.PaymentId, cancellationToken);
            }
            else
            {
                payment = await submission;
                ExpectApproved(payment, request);
            }

            var first = await TriggerAdjustmentAsync(payment.PaymentId, firstType, request.Amount, cancellationToken);
            await SendStoredCallbackAsync(payment.PaymentId, first, cancellationToken);
            if (early)
            {
                Require(!submission.IsCompleted, "The payment response arrived before the adjustment was acknowledged.");
                var beforeAdvice = await FindAsync(request.AppPaymentId, cancellationToken)
                    ?? throw new InvalidOperationException("The persisted payment disappeared.");
                ExpectUnresolved(beforeAdvice, request);
                Observe("Adjustment acknowledged before approval and Advice", beforeAdvice);
                await SendAdviceAsync(payment.PaymentId, cancellationToken);
                Require(!submission.IsCompleted, "The payment response arrived before early Advice was acknowledged.");
                var afterAdvice = await FindAsync(request.AppPaymentId, cancellationToken)
                    ?? throw new InvalidOperationException("The persisted payment disappeared.");
                ExpectUnresolved(afterAdvice, request);
                Observe("Adjustment and Advice await authoritative AuthAmount", afterAdvice);
                Log("A", "Still waiting for the delayed approval response; the service will reconsider pending events when AuthAmount arrives...");
                var reply = await submission;
                ExpectAdjusted(reply, request, firstState);
            }

            var adjusted = await ReconciledAsync(request.AppPaymentId, firstState, cancellationToken);
            ExpectAdjusted(adjusted, request, firstState);
            Observe("After " + firstType, adjusted);

            string? second = null;
            if (name is "refund-then-reversal" or "reversal-then-refund")
            {
                var secondType = firstType == "Refund" ? "Reversal" : "Refund";
                second = await TriggerAdjustmentAsync(payment.PaymentId, secondType, request.Amount, cancellationToken);
                await SendStoredCallbackAsync(payment.PaymentId, second, cancellationToken);
                adjusted = await ReconciledAsync(request.AppPaymentId, "Reversed", cancellationToken);
                ExpectAdjusted(adjusted, request, "Reversed");
                Observe("Full reversal takes precedence in " + name, adjusted);
            }

            // These are real duplicate deliveries from the stub, retaining their original
            // bytes/identity. No signed notification is constructed in this runner.
            Log("B", "Replaying stored adjustment callbacks and late approval Advice to check the adjusted result stays unchanged.");
            await SendStoredCallbackAsync(payment.PaymentId, first, cancellationToken);
            if (second is not null)
                await SendStoredCallbackAsync(payment.PaymentId, second, cancellationToken);
            await SendAdviceAsync(payment.PaymentId, cancellationToken);
            await SendAdviceAsync(payment.PaymentId, cancellationToken);
            var afterReplays = await ReconciledAsync(request.AppPaymentId, adjusted.State, cancellationToken);
            Require(afterReplays == adjusted, "Adjustment replay or late approval Advice changed an adjusted payment.");
            Observe("After adjustment replay and late approval Advice", afterReplays);
        }
        finally
        {
            pending.Cancel();
            try { await submission; }
            catch (Exception) when (submission.IsCanceled || submission.IsFaulted) { }
        }
    }

    private async Task<string> TriggerAdjustmentAsync(Guid id, string type, long amount, CancellationToken cancellationToken)
    {
        Log("B", $"Triggering full {type} in Acme for paymentId={id:D}, amount={amount} minor units...");
        using var response = await acme.PostAsJsonAsync($"/__stub/transactions/{id:D}/{type.ToLowerInvariant()}",
            new { Amount = amount }, cancellationToken);
        await CheckAsync(response, "Generate full " + type + " in Acme", cancellationToken);
        var generated = await response.Content.ReadFromJsonAsync<TriggeredAdjustment>(cancellationToken)
            ?? throw new InvalidOperationException("Acme returned no adjustment identity.");
        Require(!string.IsNullOrWhiteSpace(generated.TransactionUUID) && !string.IsNullOrWhiteSpace(generated.Rrn),
            "Acme returned an adjustment without its switch identity or original RRN.");
        Log("B", $"Acme generated {type}: transactionUUID={generated.TransactionUUID}, rrn={generated.Rrn}");
        return generated.TransactionUUID;
    }

    private static void ExpectAdjusted(ObservedPayment payment, DemoPaymentRequest request, string state)
    {
        ExpectIdentity(payment, request);
        Require(payment.State == state && payment.ApprovedAmount == request.Amount
            && payment.RemainingAmount == 0 && payment.IsFullyReconciled,
            $"Expected reconciled {state}, the original approved amount, and zero remaining value.");
    }

    private async Task<ObservedPayment> SubmitAsync(DemoPaymentRequest request, CancellationToken cancellationToken)
    {
        Log("A", $"Calling PaymentIntegration: POST {new Uri(payments.BaseAddress!, "/payments")}");
        Log("A", "Request payload:\n" + JsonSerializer.Serialize(request, DisplayJson));
        Log("A", "Waiting for PaymentIntegration's HTTP response...");
        using var response = await payments.PostAsJsonAsync("/payments", request, cancellationToken);
        Log("A", $"PaymentIntegration call finished: HTTP {(int)response.StatusCode}.");
        await CheckAsync(response, "Submit payment", cancellationToken);
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken);
        Log("A", "Response payload:\n" + JsonSerializer.Serialize(payload, DisplayJson));
        var payment = payload.Deserialize<ObservedPayment>(Json)
            ?? throw new InvalidOperationException("Payment submission returned no result.");
        return payment;
    }

    private async Task<ObservedPayment?> FindAsync(string appPaymentId, CancellationToken cancellationToken) =>
        await GetAsync<ObservedPayment>(payments, "/payments?appPaymentId=" + Uri.EscapeDataString(appPaymentId), cancellationToken);

    private Task<ObservedPayment> ReconciledAsync(string appPaymentId, string state, CancellationToken cancellationToken)
    {
        Log("Observe", $"Waiting for stored state={state} with no pending events (GET /payments)...");
        return WaitAsync(() => FindAsync(appPaymentId, cancellationToken),
            item => item.State == state && item.IsFullyReconciled, $"stored {state} payment with no pending events", cancellationToken);
    }

    private Task<ObservedTransaction?> FindTransactionAsync(Guid id, CancellationToken cancellationToken) =>
        GetAsync<ObservedTransaction>(acme, $"/__stub/transactions/{id:D}", cancellationToken);

    private async Task<ObservedTransaction> TransactionAsync(Guid id, CancellationToken cancellationToken) =>
        await FindTransactionAsync(id, cancellationToken)
        ?? throw new InvalidOperationException("Acme has no matching transaction.");

    private async Task<ObservedCallback> AdviceAsync(Guid id, CancellationToken cancellationToken)
    {
        var callbacks = await WaitAsync(() => GetAsync<ObservedCallback[]>(acme,
                $"/__stub/transactions/{id:D}/callbacks", cancellationToken),
            items => items.Any(item => item.Kind == "PaymentAdvice"), "Acme to generate Payment Advice", cancellationToken);
        return callbacks.Single(item => item.Kind == "PaymentAdvice");
    }

    private async Task SendAdviceAsync(Guid id, CancellationToken cancellationToken)
    {
        var before = await AdviceAsync(id, cancellationToken);
        await SendStoredCallbackAsync(id, before.Identifier, cancellationToken);
    }

    private async Task<ObservedCallback> CallbackAsync(Guid id, string identifier, CancellationToken cancellationToken)
    {
        var callbacks = await WaitAsync(() => GetAsync<ObservedCallback[]>(acme,
                $"/__stub/transactions/{id:D}/callbacks", cancellationToken),
            items => items.Any(item => item.Identifier == identifier), "Acme to retain the generated callback", cancellationToken);
        return callbacks.Single(item => item.Identifier == identifier);
    }

    private async Task SendStoredCallbackAsync(Guid id, string identifier, CancellationToken cancellationToken)
    {
        var before = await CallbackAsync(id, identifier, cancellationToken);
        var receiver = before.Kind == "PaymentAdvice" ? "/notifications/payment-advice" : "/notifications/transaction-status";
        Log("B", $"Asking Acme to send stored {before.Kind} callback {before.Identifier} to PaymentIntegration {receiver}...");
        using var response = await acme.PostAsync(
            $"/__stub/transactions/{id:D}/callbacks/{Uri.EscapeDataString(before.Identifier)}/send", null, cancellationToken);
        await CheckAsync(response, "Send stored " + before.Kind, cancellationToken);
        Log("B", $"Acme queued the callback (HTTP {(int)response.StatusCode}); waiting for PaymentIntegration to acknowledge after commit...");
        var acknowledged = await WaitAsync(async () => await CallbackAsync(id, identifier, cancellationToken),
            item =>
            {
                if (item.Kind == "TransactionStatus" && item.LastHttpStatusCode == 404)
                    throw new InvalidOperationException("Transaction Status receiver returned 404. Include the published Task 1 Step 6 code and deploy its SQL before running adjustment demonstrations.");
                return item.Acknowledgements > before.Acknowledgements;
            }, before.Kind + " acknowledgement after service commit", cancellationToken);
        Require(acknowledged.LastHttpStatusCode == 200, "The service did not acknowledge " + before.Kind + " with HTTP 200.");
        Log("B", $"PaymentIntegration acknowledged callback {acknowledged.Identifier}: completedAttempts={acknowledged.CompletedAttempts}, acknowledgements={acknowledged.Acknowledgements}, lastHTTP={acknowledged.LastHttpStatusCode}");
    }

    private static async Task<T?> GetAsync<T>(HttpClient client, string path, CancellationToken cancellationToken) where T : class
    {
        using var response = await client.GetAsync(path, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        await CheckAsync(response, "GET " + path, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("GET " + path + " returned no JSON result.");
    }

    private static async Task<T> WaitAsync<T>(Func<Task<T?>> read, Func<T, bool> ready,
        string description, CancellationToken cancellationToken) where T : class
    {
        var timer = Stopwatch.StartNew();
        T? observed = null;
        while (timer.Elapsed < TimeSpan.FromSeconds(15))
        {
            observed = await read();
            if (observed is not null && ready(observed))
                return observed;
            await Task.Delay(100, cancellationToken);
        }
        throw new InvalidOperationException($"Timed out waiting for {description}. Last observation: {JsonSerializer.Serialize(observed, Json)}");
    }

    private static async Task HealthAsync(HttpClient client, string name, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync("/health", cancellationToken);
        await CheckAsync(response, name + " health", cancellationToken);
    }

    private static async Task CheckAsync(HttpResponseMessage response, string operation, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{operation} returned HTTP {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(cancellationToken)}");
    }

    private void ExpectApproved(ObservedPayment payment, DemoPaymentRequest request)
    {
        ExpectIdentity(payment, request);
        Require(payment.State == "Approved" && payment.ApprovedAmount == request.Amount
            && payment.RemainingAmount == request.Amount && payment.IsFullyReconciled,
            "Expected the full approved amount and a reconciled Approved state.");
    }

    private static void ExpectDeclined(ObservedPayment payment, DemoPaymentRequest request)
    {
        ExpectIdentity(payment, request);
        Require(payment.State == "Declined" && payment.ApprovedAmount is null
            && payment.RemainingAmount is null && payment.IsFullyReconciled,
            "Expected a reconciled acquirer decline with unknown approved/remaining amounts.");
    }

    private static void ExpectUnresolved(ObservedPayment payment, DemoPaymentRequest request)
    {
        ExpectIdentity(payment, request);
        Require(payment.State == "Unresolved" && payment.ApprovedAmount is null
            && payment.RemainingAmount is null && !payment.IsFullyReconciled,
            "Expected an Unresolved payment without an authoritative approved amount.");
    }

    private static void ExpectIdentity(ObservedPayment payment, DemoPaymentRequest request) =>
        Require(payment.PaymentId != Guid.Empty && payment.AppPaymentId == request.AppPaymentId
            && payment.RequestedAmount == request.Amount && payment.CurrencyCode == request.CurrencyCode,
            "The service returned a different payment identity, currency or requested amount.");

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private void Observe(string description, ObservedPayment payment) =>
        Log("Observe", description + ":\n" + JsonSerializer.Serialize(payment, DisplayJson));

    private void Log(string flow, string message) =>
        log.WriteLine(FormattableString.Invariant($"[{elapsed.Elapsed.TotalSeconds,6:0.0}s] [{flow}] {message}"));
}

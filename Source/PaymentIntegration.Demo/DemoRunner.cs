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
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task RunAsync(string name, CancellationToken cancellationToken)
    {
        await HealthAsync(payments, "PaymentIntegration", cancellationToken);
        await HealthAsync(acme, "Acme", cancellationToken);
        var previous = await GetAsync<Scenario>(acme, "/__stub/scenario", cancellationToken)
            ?? throw new InvalidOperationException("Enable ACME_ENABLE_CONTROLS in the dedicated demo stub.");
        var request = new DemoPaymentRequest($"demo-{name}-{Guid.NewGuid():N}", settings.CustomerId,
            settings.CustomerCardId, settings.SubMerchantId, settings.Amount, settings.CurrencyCode);
        await output.WriteLineAsync($"Scenario {name}; appPaymentId={request.AppPaymentId}");
        try
        {
            var delay = name == "timeout" ? 40 : name == "out-of-order" ? 15 : 0;
            await ConfigureAsync(new(name == "decline" ? "Declined" : "Approved", delay), cancellationToken);
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
                var repeated = await SubmitAsync(request, cancellationToken);
                Require(repeated == submitted, "Repeating the same app payment changed its stored result.");
                var transaction = await TransactionAsync(submitted.PaymentId, cancellationToken);
                Require(transaction.SubmissionCount == 1, "The service submitted the payment to Acme more than once.");
                await output.WriteLineAsync($"Acme observed submissions={transaction.SubmissionCount}; same paymentId={repeated.PaymentId}");
            }

            await SendAdviceAsync(submitted.PaymentId, cancellationToken);
            var before = await ReconciledAsync(request.AppPaymentId, submitted.State, cancellationToken);
            if (name == "duplicate-notification")
            {
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
            await ConfigureAsync(previous, cleanup.Token);
        }
    }

    private async Task DelayedAsync(string name, DemoPaymentRequest request, CancellationToken cancellationToken)
    {
        using var pending = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var submission = SubmitAsync(request, pending.Token);
        try
        {
            var initial = await WaitAsync(() => FindAsync(request.AppPaymentId, cancellationToken),
                _ => true, "payment to be persisted", cancellationToken);
            ExpectUnresolved(initial, request);
            Observe("During delayed response", initial);
            await AdviceAsync(initial.PaymentId, cancellationToken);

            if (name == "out-of-order")
            {
                await SendAdviceAsync(initial.PaymentId, cancellationToken);
                Require(!submission.IsCompleted, "The original reply arrived before the out-of-order advice was observed.");
                var afterAdvice = await FindAsync(request.AppPaymentId, cancellationToken)
                    ?? throw new InvalidOperationException("The persisted payment disappeared.");
                ExpectUnresolved(afterAdvice, request);
                Observe("Advice acknowledged before authoritative AuthAmount", afterAdvice);
            }

            var result = await submission;
            ExpectApproved(result, request);
            if (name == "timeout")
            {
                var transaction = await WaitAsync(() => FindTransactionAsync(initial.PaymentId, cancellationToken),
                    item => item.ResponseInterrupted, "Acme to observe the interrupted payment response", cancellationToken);
                Require(transaction.SubmissionCount == 1 && transaction.ResponseDelaySeconds == 40,
                    "The timeout scenario did not retain a single delayed Acme submission.");
                await output.WriteLineAsync("Acme observed an interrupted reply; the service recovered approval before any advice was released.");
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
        using var response = await acme.PostAsJsonAsync("/__stub/scenario", scenario, cancellationToken);
        await CheckAsync(response, "Configure Acme scenario", cancellationToken);
    }

    private async Task<ObservedPayment> SubmitAsync(DemoPaymentRequest request, CancellationToken cancellationToken)
    {
        using var response = await payments.PostAsJsonAsync("/payments", request, cancellationToken);
        await CheckAsync(response, "Submit payment", cancellationToken);
        var payment = await response.Content.ReadFromJsonAsync<ObservedPayment>(cancellationToken)
            ?? throw new InvalidOperationException("Payment submission returned no result.");
        Observe($"POST /payments HTTP {(int)response.StatusCode}", payment);
        return payment;
    }

    private async Task<ObservedPayment?> FindAsync(string appPaymentId, CancellationToken cancellationToken) =>
        await GetAsync<ObservedPayment>(payments, "/payments?appPaymentId=" + Uri.EscapeDataString(appPaymentId), cancellationToken);

    private Task<ObservedPayment> ReconciledAsync(string appPaymentId, string state, CancellationToken cancellationToken) =>
        WaitAsync(() => FindAsync(appPaymentId, cancellationToken),
            item => item.State == state && item.IsFullyReconciled, $"stored {state} payment with no pending events", cancellationToken);

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
        using var response = await acme.PostAsync(
            $"/__stub/transactions/{id:D}/callbacks/{Uri.EscapeDataString(before.Identifier)}/send", null, cancellationToken);
        await CheckAsync(response, "Send stored Payment Advice", cancellationToken);
        var acknowledged = await WaitAsync(async () => await AdviceAsync(id, cancellationToken),
            item => item.Acknowledgements > before.Acknowledgements,
            "Payment Advice acknowledgement after service commit", cancellationToken);
        Require(acknowledged.LastHttpStatusCode == 200, "The service did not acknowledge Payment Advice with HTTP 200.");
        await output.WriteLineAsync($"Callback {acknowledged.Identifier}: completedAttempts={acknowledged.CompletedAttempts}, acknowledgements={acknowledged.Acknowledgements}, lastHTTP={acknowledged.LastHttpStatusCode}");
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
        output.WriteLine(description + ": " + JsonSerializer.Serialize(payment, Json));
}

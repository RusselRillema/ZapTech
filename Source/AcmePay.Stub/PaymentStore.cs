using System.Security.Cryptography;
using System.Text.Json;

namespace AcmePay.Stub;

public sealed class PaymentStore(
    StubSettings settings, TimeProvider clock, CallbackDispatcher callbacks, DemoScenarioState scenarios)
{
    private sealed class StoredCallback(CallbackDelivery delivery, bool isHeld)
    {
        public CallbackDelivery Delivery { get; } = delivery;
        public bool IsHeld { get; set; } = isHeld;
    }

    private sealed class Payment(PaymentResponse response, DateTimeOffset acceptedAt, string currency, DemoScenario scenario)
    {
        public PaymentResponse Original { get; } = response;
        public PaymentResponse Current { get; set; } = response;
        public DateTimeOffset AcceptedAt { get; } = acceptedAt;
        public string Currency { get; } = currency;
        public DemoScenario Scenario { get; } = scenario;
        public bool HoldCallbacks => Scenario.HoldCallbacks;
        public int SubmissionCount { get; set; } = 1;
        public bool ResponseInterrupted { get; set; }
        public HashSet<string> StatusTriggers { get; } = new(StringComparer.Ordinal);
        public List<StoredCallback> Callbacks { get; } = [];
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Payment> _transactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Payment> _rrns = new(StringComparer.Ordinal);

    public async Task<IResult> SubmitAsync(PaymentEnvelope envelope, CancellationToken cancellationToken)
    {
        var request = envelope.PaymentRequest!;
        var channel = envelope.MessageHeader!.Channel!;
        var transactionId = request.TransactionID!;
        var orderNumber = request.OrderNumber!;
        var now = clock.GetUtcNow();
        IResult result;
        TimeSpan responseDelay;
        Payment accepted;
        lock (_gate)
        {
            if (_transactions.TryGetValue(transactionId, out var previous)
                && now - previous.AcceptedAt < TimeSpan.FromHours(24))
            {
                previous.SubmissionCount++;
                return Wire.Result(previous.Original, clock, channel);
            }

            var rrn = orderNumber[..Math.Min(12, orderNumber.Length)];
            if (_rrns.TryGetValue(rrn, out var owner))
                return Wire.Error(409, "E09", "Retrieval Reference Number already in use", clock,
                    owner.Original.TransactionID, orderNumber, channel);

            // Capture one immutable scenario after replay checks; later configuration changes
            // affect new transactions, never an already accepted payment or its callbacks.
            var scenario = scenarios.Current;
            var approved = scenario.PaymentOutcome == "Approved";
            var detail = approved
                ? new ResponseDetail("Acquirer", "00", "Approved or completed successfully", "Approved.")
                : new ResponseDetail("Acquirer", "05", "Do not honour", "Your bank declined this payment.");
            var response = new PaymentResponse(transactionId, orderNumber, "Success", detail)
            {
                TransactionDateTime = Wire.Timestamp(now),
                ReconID = Guid.NewGuid().ToString("N"),
                RetrievalReferenceNumber = rrn,
                AuthCode = approved ? RandomNumberGenerator.GetInt32(100000, 1000000).ToString() : null,
                AuthAmount = approved ? request.Amount : null
            };
            var currency = request.CurrencyCode ?? "ZAR";
            var advice = new PaymentAdvice("Payment", settings.MerchantId.ToString(), transactionId,
                response.TransactionDateTime, orderNumber, response.ReconID, rrn,
                RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6"), request.Amount!.Value,
                currency, request.PaymentService!, "Success", "Acquirer", detail.Code, detail.Description, response.AuthCode);
            var delivery = new CallbackDelivery(JsonSerializer.SerializeToUtf8Bytes(advice, Wire.Json), true, transactionId);
            if (!scenario.HoldCallbacks && !callbacks.TryEnqueue(delivery))
                return Wire.Error(503, "E50", "The gateway is degraded.", clock, transactionId, orderNumber, channel);

            // Retain no card token or email in the transaction store or callback payload.
            var payment = new Payment(response, now, currency, scenario);
            payment.Callbacks.Add(new(delivery, scenario.HoldCallbacks));
            _transactions[transactionId] = payment;
            _rrns.Add(rrn, payment);
            result = Wire.Result(response, clock, channel);
            responseDelay = TimeSpan.FromSeconds(scenario.ResponseDelaySeconds);
            accepted = payment;
        }

        // Simulate an accepted payment whose reply is slow/lost. Keep its outcome and
        // callbacks even if the caller times out; RTTQ and controls remain available.
        try
        {
            if (responseDelay > TimeSpan.Zero)
                await Task.Delay(responseDelay, clock, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_gate)
                accepted.ResponseInterrupted = true;
            throw;
        }
        return result;
    }

    public IResult Query(string merchantId, string? transactionId, string? orderNumber)
    {
        if (!Guid.TryParse(merchantId, out var merchant) || merchant != settings.MerchantId)
            return Results.NotFound();
        lock (_gate)
        {
            Payment? payment = null;
            if (!string.IsNullOrEmpty(transactionId))
                _transactions.TryGetValue(transactionId, out payment);
            else if (!string.IsNullOrEmpty(orderNumber))
            {
                _rrns.TryGetValue(orderNumber[..Math.Min(12, orderNumber.Length)], out payment);
                if (payment?.Original.OrderNumber != orderNumber)
                    payment = null;
            }
            return payment is null ? Results.NotFound() : Wire.Result(payment.Current, clock);
        }
    }

    public IResult SendStatus(string transactionId, string type, long amount)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(transactionId, out var payment))
                return Results.NotFound();
            if (payment.Original.TransactionStatus != "Success" || payment.Original.ResponseDetail.Code != "00"
                || payment.StatusTriggers.Contains(type))
                return Results.Conflict(new { error = "Requires an approved payment and one trigger per adjustment type; use callback replay for duplicate deliveries." });
            if (amount != payment.Original.AuthAmount)
            {
                //TODO: Support partial refund scenarios and their lifecycle semantics when that work is requested.
                throw new NotImplementedException("Only full-amount reversal and refund callbacks are implemented.");
            }

            var status = new StatusEvent(Guid.NewGuid().ToString(), type, Wire.Timestamp(clock.GetUtcNow()),
                payment.Original.RetrievalReferenceNumber!, amount, payment.Currency, "00", true);
            var delivery = new CallbackDelivery(JsonSerializer.SerializeToUtf8Bytes(status, Wire.Json), false, status.TransactionUUID);
            if (!payment.HoldCallbacks && !callbacks.TryEnqueue(delivery))
                return Results.StatusCode(503);

            payment.Callbacks.Add(new(delivery, payment.HoldCallbacks));
            payment.StatusTriggers.Add(type);
            // Both full notifications can be generated to demonstrate precedence.
            // A refund notification after a full reversal does not undo that reversal.
            payment.Current = payment.Current with
            {
                TransactionStatus = payment.StatusTriggers.Contains("Reversal") ? "Reversed" : "Refunded"
            };
            return Results.Accepted(value: new { transactionUUID = status.TransactionUUID, rrn = status.Rrn });
        }
    }

    public IResult GetCallbacks(string transactionId)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(transactionId, out var payment))
                return Results.NotFound();

            return Results.Ok(payment.Callbacks.Select(callback =>
            {
                var statistics = callbacks.GetStatistics(callback.Delivery);
                return new
                {
                    identifier = callback.Delivery.Identifier,
                    kind = callback.Delivery.IsAdvice ? "PaymentAdvice" : "TransactionStatus",
                    isHeld = callback.IsHeld,
                    statistics.CompletedAttempts,
                    statistics.Acknowledgements,
                    statistics.LastHttpStatusCode
                };
            }).ToArray());
        }
    }

    public IResult GetObservation(string transactionId)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(transactionId, out var payment))
                return Results.NotFound();
            return Results.Ok(new
            {
                payment.SubmissionCount,
                payment.ResponseInterrupted,
                payment.Scenario.ResponseDelaySeconds
            });
        }
    }

    public IResult SendCallback(string transactionId, string identifier)
    {
        lock (_gate)
        {
            if (!_transactions.TryGetValue(transactionId, out var payment))
                return Results.NotFound();
            var callback = payment.Callbacks.Find(item => item.Delivery.Identifier == identifier);
            if (callback is null)
                return Results.NotFound();
            if (!callbacks.TryEnqueue(callback.Delivery))
                return Results.StatusCode(503);

            // The same control releases held deliveries or deliberately replays them.
            // Enqueuing a replay never applies the reversal/refund again.
            callback.IsHeld = false;
            return Results.Accepted(value: new { identifier });
        }
    }
}

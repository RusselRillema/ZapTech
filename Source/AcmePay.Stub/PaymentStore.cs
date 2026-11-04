using System.Security.Cryptography;
using System.Text.Json;

namespace AcmePay.Stub;

public sealed class PaymentStore(StubSettings settings, TimeProvider clock, CallbackDispatcher callbacks)
{
    private sealed class Payment(PaymentResponse response, DateTimeOffset acceptedAt, string currency)
    {
        public PaymentResponse Original { get; } = response;
        public PaymentResponse Current { get; set; } = response;
        public DateTimeOffset AcceptedAt { get; } = acceptedAt;
        public string Currency { get; } = currency;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Payment> _transactions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Payment> _rrns = new(StringComparer.Ordinal);

    public IResult Submit(PaymentEnvelope envelope)
    {
        var request = envelope.PaymentRequest!;
        var channel = envelope.MessageHeader!.Channel!;
        var transactionId = request.TransactionID!;
        var orderNumber = request.OrderNumber!;
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_transactions.TryGetValue(transactionId, out var previous)
                && now - previous.AcceptedAt < TimeSpan.FromHours(24))
                return Wire.Result(previous.Original, clock, channel);

            var rrn = orderNumber[..Math.Min(12, orderNumber.Length)];
            if (_rrns.TryGetValue(rrn, out var owner))
                return Wire.Error(409, "E09", "Retrieval Reference Number already in use", clock,
                    owner.Original.TransactionID, orderNumber, channel);

            var response = new PaymentResponse(transactionId, orderNumber, "Success",
                new ResponseDetail("Acquirer", "00", "Approved or completed successfully", "Approved."))
            {
                TransactionDateTime = Wire.Timestamp(now),
                ReconID = Guid.NewGuid().ToString("N"),
                RetrievalReferenceNumber = rrn,
                AuthCode = RandomNumberGenerator.GetInt32(100000, 1000000).ToString(),
                AuthAmount = request.Amount
            };
            var currency = request.CurrencyCode ?? "ZAR";
            var advice = new PaymentAdvice("Payment", settings.MerchantId.ToString(), transactionId,
                response.TransactionDateTime, orderNumber, response.ReconID, rrn,
                RandomNumberGenerator.GetInt32(0, 1000000).ToString("D6"), request.Amount!.Value,
                currency, request.PaymentService!, "Success", "Acquirer", "00", "Approved", response.AuthCode);
            if (!callbacks.TryEnqueue(new(JsonSerializer.SerializeToUtf8Bytes(advice, Wire.Json), true, transactionId)))
                return Wire.Error(503, "E50", "The gateway is degraded.", clock, transactionId, orderNumber, channel);

            // Retain no card token or email in the transaction store or callback payload.
            var payment = new Payment(response, now, currency);
            _transactions[transactionId] = payment;
            _rrns.Add(rrn, payment);
            return Wire.Result(response, clock, channel);
        }
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
            if (payment.Current.TransactionStatus != "Success")
                return Results.Conflict(new { error = "This happy-path trigger requires an approved payment." });
            if (amount != payment.Original.AuthAmount)
            {
                //TODO: Support partial refund scenarios and their lifecycle semantics when that work is requested.
                throw new NotImplementedException("Only full-amount reversal and refund callbacks are implemented.");
            }

            var status = new StatusEvent(Guid.NewGuid().ToString(), type, Wire.Timestamp(clock.GetUtcNow()),
                payment.Original.RetrievalReferenceNumber!, amount, payment.Currency, "00", true);
            if (!callbacks.TryEnqueue(new(JsonSerializer.SerializeToUtf8Bytes(status, Wire.Json), false, status.TransactionUUID)))
                return Results.StatusCode(503);

            payment.Current = payment.Current with { TransactionStatus = type == "Reversal" ? "Reversed" : "Refunded" };
            return Results.Accepted(value: new { transactionUUID = status.TransactionUUID, rrn = status.Rrn });
        }
    }
}

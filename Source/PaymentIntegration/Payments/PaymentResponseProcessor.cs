using System.Text.Json;

namespace PaymentIntegration.Payments;

public sealed class PaymentResponseProcessor
{
    public (Payment Payment, PaymentEvent Event) Process(Payment payment, PaymentEvent receipt,
        DateTimeOffset processedAt,
        bool retryingRateLimit = false)
    {
        if (receipt.Source == "PaymentAdvice")
            return ProcessAdvice(payment, receipt, processedAt);

        if (receipt.Source != "PaymentResponse")
        {
            //TODO: Implement query and reversal/refund processing in their respective steps.
            throw new NotImplementedException($"Event source {receipt.Source} is not implemented yet.");
        }

        var hasOutcome = payment.State is "Approved" or "Declined" or "Failed";
        // A 409 describes rejection of this request even though its TransactionID
        // names the transaction already holding the RRN. Never adopt that ID.
        if (receipt.HttpStatusCode is 400 or 401 or 409 or 429)
        {
            if (hasOutcome)
            {
                return Ignore(payment, receipt, processedAt,
                    "Acme rejection cannot replace an established payment outcome.");
            }

            if (retryingRateLimit && IsRateLimited(receipt))
            {
                return LeavePending(payment, receipt,
                    "Acme rate limited this attempt; awaiting a retry outcome.");
            }

            return Apply(
                payment with
                {
                    State = "Failed",
                    ApprovedAmount = null,
                    RemainingAmount = null
                },
                receipt,
                processedAt);
        }

        if (receipt.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("PaymentResponse", out var response)
            || response.ValueKind != JsonValueKind.Object)
        {
            if (hasOutcome)
            {
                return Ignore(payment, receipt, processedAt,
                    "A payment outcome is already known; this receipt contains no outcome.");
            }

            return LeavePending(payment, receipt,
                "No usable Acme payment response; outcome remains unknown.");
        }

        var transactionId = Text(response, "TransactionID");
        if (transactionId is not null && (!Guid.TryParse(transactionId, out var id) || id != payment.PaymentId))
        {
            return Ignore(payment, receipt, processedAt,
                "Acme response belongs to a different transaction.");
        }

        if (Text(response, "OrderNumber") is { } orderNumber && orderNumber != payment.OrderNumber)
        {
            return Ignore(payment, receipt, processedAt,
                "Acme response belongs to a different order.");
        }

        if (transactionId is null || receipt.HttpStatusCode is not (>= 200 and < 300)
            || !response.TryGetProperty("ResponseDetail", out var detail)
            || detail.ValueKind != JsonValueKind.Object || Text(detail, "Source") != "Acquirer"
            || string.IsNullOrWhiteSpace(Text(detail, "Code")))
        {
            if (hasOutcome)
            {
                return Ignore(payment, receipt, processedAt,
                    "A payment outcome is already known; this receipt establishes no bank outcome.");
            }

            return LeavePending(payment, receipt,
                "Acme response does not establish an acquirer outcome.");
        }

        // TransactionStatus describes gateway processing. The acquirer code decides the outcome.
        var approved = Text(detail, "Code") == "00";
        long? amount = null;
        if (approved)
        {
            if (!response.TryGetProperty("AuthAmount", out var authorised)
                || authorised.ValueKind != JsonValueKind.Number || !authorised.TryGetInt64(out var value) || value < 0)
            {
                return LeavePending(payment, receipt,
                    "Approval has no valid AuthAmount; approved value remains unknown.");
            }

            amount = value;
        }

        var nextState = approved ? "Approved" : "Declined";
        if (hasOutcome)
        {
            if (payment.State == nextState && (!approved || payment.ApprovedAmount == amount))
            {
                return Ignore(payment, receipt, processedAt,
                    "This bank outcome has already been applied.");
            }

            return LeavePending(payment, receipt,
                "Conflicting bank outcomes require reconciliation.");
        }

        return Apply(
            payment with
            {
                State = nextState,
                ApprovedAmount = amount,
                RemainingAmount = amount
            },
            receipt,
            processedAt);
    }

    public PaymentEvent ProcessUnlinkedAdvice(PaymentEvent receipt, DateTimeOffset processedAt)
    {
        if (receipt.Payload is { ValueKind: JsonValueKind.Object } payload
            && Text(payload, "RequestType") is { } requestType && requestType != "Payment")
        {
            return receipt with
            {
                ProcessingState = "Ignored",
                ProcessingComment = "UnsupportedRequestType",
                ProcessedAt = processedAt
            };
        }
        return receipt with { ProcessingComment = "UnknownPayment" };
    }

    private (Payment Payment, PaymentEvent Event) ProcessAdvice(
        Payment payment, PaymentEvent receipt, DateTimeOffset processedAt)
    {
        if (receipt.Payload is not { ValueKind: JsonValueKind.Object } advice)
            return LeavePending(payment, receipt, "No usable Payment Advice payload.");
        if (Text(advice, "RequestType") != "Payment")
            return Ignore(payment, receipt, processedAt, "UnsupportedRequestType");
        // Acme's conventions specify ZAR when CurrencyCode is omitted.
        if ((Text(advice, "CurrencyCode") ?? "ZAR") != payment.CurrencyCode)
            return Ignore(payment, receipt, processedAt, "Payment Advice currency differs from the payment currency.");
        if (!Guid.TryParse(Text(advice, "TransactionID"), out var id) || id != payment.PaymentId)
            return Ignore(payment, receipt, processedAt, "Payment Advice belongs to a different transaction.");
        if (Text(advice, "OrderNumber") is { } order && order != payment.OrderNumber)
            return Ignore(payment, receipt, processedAt, "Payment Advice belongs to a different order.");
        if (Text(advice, "RetrievalReferenceNumber") != payment.OrderNumber[..Math.Min(12, payment.OrderNumber.Length)])
            return Ignore(payment, receipt, processedAt, "Payment Advice belongs to a different retrieval reference.");

        var hasOutcome = payment.State != "Unresolved";
        if (Text(advice, "ResponseSource") != "Acquirer" || string.IsNullOrWhiteSpace(Text(advice, "ResponseCode")))
        {
            return hasOutcome
                ? Ignore(payment, receipt, processedAt, "Payment Advice establishes no new acquirer outcome.")
                : LeavePending(payment, receipt, "Payment Advice does not establish an acquirer outcome.");
        }

        var approved = Text(advice, "ResponseCode") == "00";
        var nextState = approved ? "Approved" : "Declined";
        if (approved && payment.ApprovedAmount is null)
        {
            return LeavePending(payment, receipt,
                "Approval advice has no authoritative AuthAmount; awaiting a payment response or transaction query.");
        }
        if (hasOutcome)
        {
            return payment.State == nextState
                ? Ignore(payment, receipt, processedAt, "This bank outcome has already been applied.")
                : LeavePending(payment, receipt, "Conflicting bank outcomes require reconciliation.");
        }
        return Apply(payment with
        {
            State = nextState,
            ApprovedAmount = approved ? payment.ApprovedAmount : null,
            RemainingAmount = approved ? payment.ApprovedAmount : null
        }, receipt, processedAt);
    }

    private (Payment Payment, PaymentEvent Event) LeavePending(
        Payment payment,
        PaymentEvent receipt,
        string reason)
    {
        return (payment, receipt with { ProcessingComment = reason });
    }

    private (Payment Payment, PaymentEvent Event) Ignore(
        Payment payment,
        PaymentEvent receipt,
        DateTimeOffset processedAt,
        string reason)
    {
        return (payment, receipt with
        {
            ProcessingState = "Ignored",
            ProcessingComment = reason,
            ProcessedAt = processedAt
        });
    }

    private (Payment Payment, PaymentEvent Event) Apply(
        Payment payment,
        PaymentEvent receipt,
        DateTimeOffset processedAt)
    {
        return (payment, receipt with
        {
            ProcessingState = "Applied",
            ProcessingComment = null,
            ProcessedAt = processedAt
        });
    }

    private string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;

    private bool IsRateLimited(PaymentEvent receipt)
    {
        return receipt.HttpStatusCode == 429
            && receipt.Payload is { ValueKind: JsonValueKind.Object } body
            && body.TryGetProperty("PaymentResponse", out var response)
            && response.ValueKind == JsonValueKind.Object
            && response.TryGetProperty("ResponseDetail", out var detail)
            && detail.ValueKind == JsonValueKind.Object
            && Text(detail, "Code") == "E29";
    }
}

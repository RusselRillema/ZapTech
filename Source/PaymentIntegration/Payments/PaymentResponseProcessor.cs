using System.Text.Json;

namespace PaymentIntegration.Payments;

public static class PaymentResponseProcessor
{
    public static (Payment Payment, PaymentEvent Event) Process(Payment payment, PaymentEvent receipt,
        DateTimeOffset processedAt)
    {
        if (receipt.Source != "PaymentResponse")
        {
            //TODO: Implement notification and query processing in their respective steps.
            throw new NotImplementedException($"Event source {receipt.Source} is not implemented yet.");
        }

        (Payment, PaymentEvent) Pending(string reason) =>
            (payment, receipt with { ProcessingComment = reason });
        (Payment, PaymentEvent) Ignored(string reason) =>
            (payment, receipt with { ProcessingState = "Ignored", ProcessingComment = reason, ProcessedAt = processedAt });

        var hasOutcome = payment.State is "Approved" or "Declined" or "Failed";
        // A 409 describes rejection of this request even though its TransactionID
        // names the transaction already holding the RRN. Never adopt that ID.
        if (receipt.HttpStatusCode is 400 or 401 or 409 or 429)
        {
            if (hasOutcome) return Ignored("Acme rejection cannot replace an established payment outcome.");
            return (payment with { State = "Failed", ApprovedAmount = null, RemainingAmount = null },
                receipt with { ProcessingState = "Applied", ProcessingComment = null, ProcessedAt = processedAt });
        }

        if (receipt.Payload is not { ValueKind: JsonValueKind.Object } payload
            || !payload.TryGetProperty("PaymentResponse", out var response)
            || response.ValueKind != JsonValueKind.Object)
            return hasOutcome ? Ignored("A payment outcome is already known; this receipt contains no outcome.")
                : Pending("No usable Acme payment response; outcome remains unknown.");

        var transactionId = Text(response, "TransactionID");
        if (transactionId is not null && (!Guid.TryParse(transactionId, out var id) || id != payment.PaymentId))
            return Ignored("Acme response belongs to a different transaction.");
        if (Text(response, "OrderNumber") is { } orderNumber && orderNumber != payment.OrderNumber)
            return Ignored("Acme response belongs to a different order.");
        if (transactionId is null || receipt.HttpStatusCode is not (>= 200 and < 300)
            || !response.TryGetProperty("ResponseDetail", out var detail)
            || detail.ValueKind != JsonValueKind.Object || Text(detail, "Source") != "Acquirer"
            || string.IsNullOrWhiteSpace(Text(detail, "Code")))
            return hasOutcome ? Ignored("A payment outcome is already known; this receipt establishes no bank outcome.")
                : Pending("Acme response does not establish an acquirer outcome.");

        // TransactionStatus describes gateway processing. The acquirer code decides the outcome.
        var approved = Text(detail, "Code") == "00";
        long? amount = null;
        if (approved)
        {
            if (!response.TryGetProperty("AuthAmount", out var authorised)
                || authorised.ValueKind != JsonValueKind.Number || !authorised.TryGetInt64(out var value) || value < 0)
                return Pending("Approval has no valid AuthAmount; approved value remains unknown.");
            amount = value;
        }

        var nextState = approved ? "Approved" : "Declined";
        if (hasOutcome)
        {
            if (payment.State == nextState && (!approved || payment.ApprovedAmount == amount))
                return Ignored("This bank outcome has already been applied.");
            return Pending("Conflicting bank outcomes require reconciliation.");
        }

        return (payment with { State = nextState, ApprovedAmount = amount, RemainingAmount = amount },
            receipt with { ProcessingState = "Applied", ProcessingComment = null, ProcessedAt = processedAt });
    }

    private static string? Text(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString() : null;
}

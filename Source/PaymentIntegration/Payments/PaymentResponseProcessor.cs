using System.Text.Json;

namespace PaymentIntegration.Payments;

public sealed class PaymentResponseProcessor
{
    public (Payment Payment, PaymentEvent Event) Process(Payment payment, PaymentEvent receipt,
        DateTimeOffset processedAt,
        bool retryingRateLimit = false)
    {
        if (receipt.Source is not ("PaymentResponse" or "TransactionQuery"))
        {
            //TODO: Implement notification processing in its respective step.
            throw new NotImplementedException($"Event source {receipt.Source} is not implemented yet.");
        }

        var hasOutcome = payment.State != "Unresolved";
        // A 409 describes rejection of this request even though its TransactionID
        // names the transaction already holding the RRN. Never adopt that ID.
        if (receipt.Source == "PaymentResponse" && receipt.HttpStatusCode is (400 or 401 or 409 or 429))
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

        if (Text(response, "TransactionStatus") == "Processing")
        {
            return hasOutcome
                ? Ignore(payment, receipt, processedAt, "A payment outcome is already known; this receipt is still processing.")
                : LeavePending(payment, receipt, "Acme is still processing the payment; outcome remains unknown.");
        }

        if (receipt.Source == "TransactionQuery" && Text(response, "TransactionStatus") is ("Reversed" or "Refunded"))
        {
            // RTTQ is a snapshot, not the adjustment history needed to calculate remaining value.
            return LeavePending(payment, receipt, "Query reports a later lifecycle state requiring reconciliation.");
        }

        // TransactionStatus describes gateway processing. The acquirer code decides the outcome.
        var approved = Text(detail, "Code") == "00";
        long? amount = null;
        if (approved)
        {
            if (!response.TryGetProperty("AuthAmount", out var authorised)
                || authorised.ValueKind != JsonValueKind.Number || !authorised.TryGetInt64(out var value) || value < 0)
            {
                if (payment.State == "Approved" && payment.ApprovedAmount is not null)
                {
                    return Ignore(payment, receipt, processedAt,
                        "Approval has no valid AuthAmount; the authorised amount is already established.");
                }

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

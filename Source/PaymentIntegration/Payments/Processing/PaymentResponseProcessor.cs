using System.Text.Json;
using PaymentIntegration.Payments.Models;

namespace PaymentIntegration.Payments.Processing;

public sealed class PaymentResponseProcessor(TransactionStatusProcessor statusProcessor)
{
    public (Payment Payment, PaymentEvent Event) Process(Payment payment, PaymentEvent receipt,
        DateTimeOffset processedAt,
        bool retryingRateLimit = false, IReadOnlyList<PaymentEvent>? statusHistory = null)
    {
        if (receipt.Source == "PaymentAdvice")
            return ProcessAdvice(payment, receipt, processedAt);

        if (receipt.Source is not ("PaymentResponse" or "TransactionQuery"))
        {
            throw new InvalidOperationException($"Event source {receipt.Source} is not a payment outcome.");
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

        if (IsAdjustedQuery(receipt))
        {
            return ProcessAdjustedQuery(payment, receipt, response, detail, statusHistory ?? [], processedAt);
        }

        // TransactionStatus describes gateway processing. The acquirer code decides the outcome.
        var approved = Text(detail, "Code") == "00";
        long? amount = null;
        if (approved)
        {
            if (!response.TryGetProperty("AuthAmount", out var authorised)
                || authorised.ValueKind != JsonValueKind.Number || !authorised.TryGetInt64(out var value) || value < 0)
            {
                if (payment.HasApproval && payment.ApprovedAmount is not null)
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
            if (approved && payment.HasApproval && payment.ApprovedAmount == amount
                || !approved && payment.State == nextState)
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

    internal static bool IsAdjustedQuery(PaymentEvent receipt) =>
        receipt.Source == "TransactionQuery"
        && receipt.Payload is { ValueKind: JsonValueKind.Object } payload
        && payload.TryGetProperty("PaymentResponse", out var response)
        && response.ValueKind == JsonValueKind.Object
        && Text(response, "TransactionStatus") is ("Reversed" or "Refunded");

    private (Payment Payment, PaymentEvent Event) ProcessAdjustedQuery(Payment payment, PaymentEvent receipt,
        JsonElement response, JsonElement detail, IReadOnlyList<PaymentEvent> history, DateTimeOffset processedAt)
    {
        if (Text(detail, "Code") != "00")
            return LeavePending(payment, receipt,
                "Adjusted query conflicts with its acquirer outcome; investigation is required.");

        if (!response.TryGetProperty("AuthAmount", out var authorised)
            || authorised.ValueKind != JsonValueKind.Number || !authorised.TryGetInt64(out var amount) || amount < 0)
            return LeavePending(payment, receipt,
                "Adjusted query has no valid AuthAmount; approved value remains unknown.");

        if (payment.State is "Declined" or "Failed"
            || payment.HasApproval && payment.ApprovedAmount != amount)
            return LeavePending(payment, receipt,
                "Adjusted query conflicts with the established bank outcome; investigation is required.");

        // Interpret the recorded history against the authoritative amount without
        // adopting a provisional approval or balance when the evidence is incomplete.
        var result = statusProcessor.Process(payment with
        {
            State = "Approved", ApprovedAmount = amount, RemainingAmount = amount
        }, history, processedAt);
        var state = Text(response, "TransactionStatus");
        if (result.HasConflictingEvidence
            || result.Payment.State is "Reversed" or "Refunded" && result.Payment.State != state)
            return LeavePending(payment, receipt,
                "Adjusted query conflicts with recorded adjustment history; investigation is required.");

        if (result.Payment.State != state || result.Payment.RemainingAmount != 0
            || result.Events.Any(item => item.ProcessingState == "Pending"))
            return LeavePending(payment, receipt,
                "Adjusted query requires complete matching adjustment history; remaining value is unknown.");

        return Apply(result.Payment, receipt, processedAt);
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
            return (approved && payment.HasApproval || payment.State == nextState)
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

    private static string? Text(JsonElement value, string name) =>
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

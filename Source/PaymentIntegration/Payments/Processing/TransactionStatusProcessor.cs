using System.Text.Json;
using PaymentIntegration.Payments.Models;
using PaymentIntegration.Payments.Validation;

namespace PaymentIntegration.Payments.Processing;

public sealed class TransactionStatusProcessor
{
    private const string DuplicateComment = "This Acme event has already been recorded.";

    private sealed record Adjustment(PaymentEvent Receipt, string Identifier, string Type,
        string Timestamp, long Amount, string Code, bool Approved);

    public (Payment Payment, IReadOnlyList<PaymentEvent> Events, bool HasConflictingEvidence) Process(
        Payment payment, IReadOnlyList<PaymentEvent> history, DateTimeOffset processedAt)
    {
        var events = history.ToDictionary(item => item.PaymentEventId);
        var adjustments = new List<Adjustment>();

        void Set(PaymentEvent receipt, string state, string? comment) =>
            events[receipt.PaymentEventId] = Classify(receipt, state, comment, processedAt);

        foreach (var receipt in history)
        {
            if (receipt.Payload is not { } payload)
            {
                Set(receipt, "Ignored", "Transaction Status has no usable payload.");
                continue;
            }
            try
            {
                var rrn = TransactionStatusValidation.Validate(payload);
                if (rrn != payment.OrderNumber[..Math.Min(12, payment.OrderNumber.Length)])
                {
                    Set(receipt, "Ignored", "Transaction Status belongs to a different retrieval reference.");
                    continue;
                }
                if ((Text(payload, "currencyCode") ?? "ZAR") != payment.CurrencyCode)
                {
                    Set(receipt, "Ignored", "Transaction Status currency differs from the payment currency.");
                    continue;
                }
                adjustments.Add(new(receipt, Text(payload, "transactionUUID")!, Text(payload, "transactionType")!,
                    Text(payload, "transactionDateTime")!, payload.GetProperty("transactionAmount").GetInt64(),
                    Text(payload, "txResultCode")!, payload.GetProperty("isApproved").GetBoolean()));
            }
            catch (PaymentRequestException)
            {
                Set(receipt, "Ignored", "Transaction Status payload is invalid.");
            }
        }

        // Compare business fields, accepting changes in JSON layout and unknown fields.
        var groups = adjustments.GroupBy(item => item.Identifier, StringComparer.Ordinal).ToArray();
        var conflictingGroups = groups.Where(group => group.Select(Details).Distinct().Skip(1).Any()).ToArray();
        var representatives = new List<Adjustment>();
        foreach (var group in conflictingGroups)
        {
            // Issue #34: a conflicting duplicate cannot invalidate an established
            // adjustment or remove an ignored refund's limit from the history.
            var established = group.Where(item => item.Receipt.ProcessingState == "Applied"
                    || item.Receipt.ProcessingState == "Ignored" && item.Receipt.ProcessingComment != DuplicateComment)
                .OrderByDescending(item => item.Receipt.ProcessingState == "Applied")
                .ThenBy(item => item.Receipt.ReceivedAt).ThenBy(item => item.Receipt.PaymentEventId).FirstOrDefault();
            foreach (var item in group.Where(item => item.Receipt.ProcessingState == "Pending"))
            {
                if (established is not null && Details(item) == Details(established))
                    Set(item.Receipt, "Ignored", DuplicateComment);
                else
                    Set(item.Receipt, "Pending", "Acme event identifier has conflicting details; investigation is required.");
            }
            if (established is not null)
                representatives.Add(established);
        }

        var validGroups = groups.Except(conflictingGroups).ToArray();
        var conflictingOutcome = validGroups.SelectMany(group => group).Any(item => (item.Code == "00") != item.Approved);
        if (conflictingOutcome)
        {
            foreach (var item in validGroups.SelectMany(group => group))
                Set(item.Receipt, "Pending", "Switch outcome indicators conflict; investigation is required.");
            return (payment.HasApproval ? payment with { State = "Approved", RemainingAmount = null } : payment,
                events.Values.ToArray(), true);
        }

        foreach (var group in validGroups)
        {
            // Keep an already-applied receipt as the representative of a replay.
            var ordered = group.OrderByDescending(item => item.Receipt.ProcessingState == "Applied")
                .ThenBy(item => item.Receipt.ProcessingState == "Ignored")
                .ThenBy(item => item.Receipt.ReceivedAt).ThenBy(item => item.Receipt.PaymentEventId).ToArray();
            var item = ordered[0];
            foreach (var duplicate in ordered.Skip(1))
                Set(duplicate.Receipt, "Ignored", DuplicateComment);
            representatives.Add(item);
        }

        var candidates = new List<Adjustment>();
        foreach (var item in representatives)
        {
            if (!item.Approved)
                Set(item.Receipt, "Ignored", "Acme Switch did not apply this adjustment.");
            else if (item.Amount <= 0)
                Set(item.Receipt, "Ignored", "Adjustment amount must be positive.");
            else if (payment.State is "Declined" or "Failed")
                Set(item.Receipt, "Ignored", "An adjustment cannot be applied to a payment without approval.");
            else if (!payment.HasApproval || payment.ApprovedAmount is null)
                Set(item.Receipt, "Pending", "Awaiting approval with an authoritative AuthAmount.");
            else if (item.Type == "Reversal" && item.Amount != payment.ApprovedAmount)
                Set(item.Receipt, "Ignored", "Partial reversals are unsupported; a reversal must equal the approved amount.");
            else
                candidates.Add(item);
        }

        if (candidates.Count == 0)
            return (payment, events.Values.ToArray(), conflictingGroups.Length > 0);

        // Reconstruct the current value from the complete adjustment history. Never
        // subtract a replay from the already-reduced persisted balance.
        var reversal = candidates.Where(item => item.Type == "Reversal")
            .OrderBy(item => item.Timestamp, StringComparer.Ordinal)
            .ThenBy(item => item.Identifier, StringComparer.Ordinal).FirstOrDefault();
        if (reversal is not null)
        {
            foreach (var item in candidates)
            {
                Set(item.Receipt, item == reversal ? "Applied" : "Ignored", item == reversal ? null
                    : "A full reversal already accounts for the approved value.");
            }
            return (payment with { State = "Reversed", RemainingAmount = 0 }, events.Values.ToArray(), conflictingGroups.Length > 0);
        }

        var total = 0L;
        var ignoringRefunds = false;
        foreach (var refund in candidates.OrderBy(item => item.Timestamp, StringComparer.Ordinal)
                     .ThenBy(item => item.Identifier, StringComparer.Ordinal))
        {
            if (ignoringRefunds)
                Set(refund.Receipt, "Ignored", "Refund follows an over-limit event in the switch history.");
            else if (refund.Amount > payment.ApprovedAmount!.Value - total
                || refund.Receipt.ProcessingState == "Ignored")
            {
                ignoringRefunds = true;
                Set(refund.Receipt, "Ignored", "Refund is greater than the current payment value.");
            }
            else
            {
                total += refund.Amount;
                Set(refund.Receipt, "Applied", null);
            }
        }

        var state = total == 0 ? "Approved" : total == payment.ApprovedAmount ? "Refunded" : "PartiallyRefunded";
        return (payment with { State = state, RemainingAmount = payment.ApprovedAmount - total },
            events.Values.ToArray(), ignoringRefunds || conflictingGroups.Length > 0);
    }

    private static (string Type, string Timestamp, long Amount, string Code, bool Approved) Details(Adjustment item) =>
        (item.Type, item.Timestamp, item.Amount, item.Code, item.Approved);

    private static PaymentEvent Classify(PaymentEvent receipt, string state, string? comment, DateTimeOffset processedAt)
    {
        // Ignored receipts remain terminal audit records.
        if (receipt.ProcessingState == "Ignored"
            || receipt.ProcessingState == state && receipt.ProcessingComment == comment)
            return receipt;
        return receipt with
        {
            ProcessingState = state,
            ProcessingComment = comment,
            ProcessedAt = state == "Pending" ? null : processedAt
        };
    }

    private static string? Text(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

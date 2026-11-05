using PaymentIntegration.Payments;

namespace PaymentIntegration.Reports;

public sealed record HistoricalPayment(Payment Payment, DateTimeOffset? OutcomeAt,
    IReadOnlyList<PaymentEvent> Events, bool HasSubsequentActivity);

public sealed class PaymentHistoryReconstructor(PaymentEventProcessor processor,
    ILogger<PaymentHistoryReconstructor> logger)
{
    public HistoricalPayment Reconstruct(PaymentReportHistory history, ReportingPeriod period,
        CancellationToken cancellationToken)
    {
        Guid? eventId = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var timeline = history.Events.Select(receipt =>
            {
                eventId = receipt.PaymentEventId;
                return (Receipt: receipt, EffectiveAt: ReportingPeriod.EffectiveAt(receipt));
            }).OrderBy(item => item.Receipt.ReceivedAt).ThenBy(item => item.Receipt.PaymentEventId).ToArray();

            // Reuse only original details. Today's state, amounts and classifications
            // must not leak into an earlier reconstruction.
            var payment = history.Payment with
            {
                State = "Unresolved", ApprovedAmount = null, RemainingAmount = null, HasPendingEvents = false
            };
            var classified = new Dictionary<Guid, PaymentEvent>();
            DateTimeOffset? outcomeAt = null;

            Payment Apply(Payment current, PaymentEvent raw)
            {
                cancellationToken.ThrowIfCancellationRequested();
                eventId = raw.PaymentEventId;
                classified.Add(raw.PaymentEventId, raw with
                {
                    ProcessingState = "Pending", ProcessingComment = null, ProcessedAt = null
                });
                var result = processor.Process(current,
                    classified.Values.Where(item => item.ProcessingState == "Pending").ToArray(),
                    classified.Values.Where(item => item.Source == "TransactionStatus").ToArray(),
                    raw.PaymentEventId,
                    // Retry decisions are not fully audited. Keep E29 receipts pending
                    // until a later bank outcome resolves them, as agreed for reporting.
                    retryingRateLimit: true, processedAt: raw.ReceivedAt);
                foreach (var item in result.Events)
                    classified[item.PaymentEventId] = item;
                return result.Payment;
            }

            foreach (var item in timeline.Where(item => item.EffectiveAt < period.End))
            {
                var previous = payment;
                payment = Apply(payment, item.Receipt);
                if (previous.State == "Unresolved" && payment.State != "Unresolved" && outcomeAt is null)
                {
                    // The C# processor has established the bank outcome. Locate its
                    // applied receipt rather than interpreting Acme response codes again.
                    var outcome = classified.Values.Where(receipt => receipt.Source != "TransactionStatus"
                        && receipt.ProcessingState == "Applied")
                        .OrderBy(ReportingPeriod.EffectiveAt).ThenBy(receipt => receipt.ReceivedAt).FirstOrDefault();
                    if (outcome is not null)
                        outcomeAt = ReportingPeriod.EffectiveAt(outcome);
                }
            }

            var historical = payment;
            var historicalEvents = classified.Values.ToArray();
            var hasSubsequentActivity = false;
            foreach (var item in timeline.Where(item => item.EffectiveAt >= period.End))
            {
                var previous = (payment.State, payment.ApprovedAmount, payment.RemainingAmount);
                payment = Apply(payment, item.Receipt);
                hasSubsequentActivity |= previous != (payment.State, payment.ApprovedAmount, payment.RemainingAmount);
            }

            return new(historical, outcomeAt, historicalEvents, hasSubsequentActivity);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}, report date {ReportDate}",
                "reconstruct payment history", history.Payment.PaymentId, eventId, period.Date);
            throw;
        }
    }
}

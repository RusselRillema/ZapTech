namespace PaymentIntegration.Payments;

// Callers supply pending receipts and the complete adjustment history for one payment.
// Persistence, historical cutoff selection and retry decisions remain with the caller.
public sealed class PaymentEventProcessor(PaymentResponseProcessor responseProcessor,
    TransactionStatusProcessor statusProcessor)
{
    public PaymentProcessingResult Process(
        Payment payment,
        IReadOnlyList<PaymentEvent> events,
        IReadOnlyList<PaymentEvent> transactionStatusHistory,
        Guid triggeringEventId,
        bool retryingRateLimit,
        DateTimeOffset processedAt)
    {
        var pending = events.ToArray();
        if (pending.Any(item => item.PaymentId != payment.PaymentId))
            throw new ArgumentException("All events must belong to the supplied payment.", nameof(events));
        if (transactionStatusHistory.Any(item => item.PaymentId != payment.PaymentId))
            throw new ArgumentException("All adjustment history must belong to the supplied payment.", nameof(transactionStatusHistory));

        var statusHistory = transactionStatusHistory;
        bool applied;
        do
        {
            applied = false;
            for (var index = 0; index < pending.Length; index++)
            {
                if (pending[index].ProcessingState != "Pending" || pending[index].Source == "TransactionStatus")
                    continue;

                // Earlier E29 receipts await the current attempt's outcome.
                var deferRateLimitFailure = pending[index].PaymentEventId != triggeringEventId || retryingRateLimit;
                var result = responseProcessor.Process(payment, pending[index], processedAt, deferRateLimitFailure, statusHistory);
                payment = result.Payment;
                pending[index] = result.Event;
                applied |= result.Event.ProcessingState == "Applied";
            }

            if (statusHistory.Count > 0)
            {
                var previous = statusHistory.ToDictionary(item => item.PaymentEventId);
                var result = statusProcessor.Process(payment, statusHistory, processedAt);
                payment = result.Payment;
                statusHistory = result.Events;
                applied |= statusHistory.Any(item => item.ProcessingState == "Applied"
                    && previous[item.PaymentEventId].ProcessingState != "Applied");
            }
        } while (applied);

        var updatedEvents = pending.Where(item => item.Source != "TransactionStatus").Concat(statusHistory).ToArray();
        payment = payment with { HasPendingEvents = updatedEvents.Any(item => item.ProcessingState == "Pending") };
        return new PaymentProcessingResult(payment, updatedEvents);
    }
}

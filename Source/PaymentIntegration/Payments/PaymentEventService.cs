namespace PaymentIntegration.Payments;

public sealed class PaymentEventService(IPaymentRepository repository,
    PaymentResponseProcessor processor, PaymentEventProcessor eventProcessor,
    TimeProvider clock, ILogger<PaymentEventService> logger)
{
    public Task<Payment?> ProcessAsync(Guid? paymentId, Guid eventId,
        PaymentEvent? receipt, bool retryingRateLimit = false) =>
        ProcessAsync(paymentId, eventId, receipt, retryingRateLimit,
            token => repository.BeginProcessingAsync(paymentId, eventId, token));

    public Task<Payment?> ProcessStatusAsync(string rrn, Guid eventId, PaymentEvent receipt) =>
        ProcessAsync(null, eventId, receipt, false,
            token => repository.BeginProcessingByRrnAsync(rrn, eventId, token));

    private async Task<Payment?> ProcessAsync(Guid? paymentId, Guid eventId, PaymentEvent? receipt,
        bool retryingRateLimit, Func<CancellationToken, Task<IPaymentProcessingSession>> beginSession)
    {
        // Finish auditing a received event even if its HTTP caller disconnects.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        var token = timeout.Token;
        var operation = "payment row lock";

        try
        {
            await using var session = await beginSession(token);
            var payment = session.Payment;
            PaymentEvent[] investigations = [];
            paymentId = payment?.PaymentId ?? paymentId;
            if (payment is null && receipt?.Source is not ("PaymentAdvice" or "TransactionStatus"))
            {
                throw new InvalidOperationException("The payment to process does not exist.");
            }

            if (receipt is not null)
            {
                receipt = receipt with { PaymentId = payment?.PaymentId };
                operation = "insert payment event";
                await session.InsertEventAsync(receipt, token);
            }

            if (payment is null)
            {
                operation = "save unlinked payment event";
                var unlinked = receipt!.Source == "PaymentAdvice"
                    ? processor.ProcessUnlinkedAdvice(receipt, clock.GetUtcNow())
                    : receipt with { ProcessingComment = "UnknownPayment" };
                await session.SaveEventAsync(unlinked, token);
            }
            else
            {
                operation = "read pending payment events";
                var pending = (await session.GetPendingEventsAsync(token)).ToArray();
                IReadOnlyList<PaymentEvent> statusHistory = [];
                if (pending.Any(item => item.Source == "TransactionStatus" || PaymentResponseProcessor.IsAdjustedQuery(item)))
                {
                    operation = "read transaction status history";
                    statusHistory = await session.GetTransactionStatusEventsAsync(token);
                }
                operation = "process payment event batch";
                var result = eventProcessor.Process(payment, pending, statusHistory, eventId, retryingRateLimit, clock.GetUtcNow());
                payment = result.Payment;
                var updatedEvents = result.Events;
                investigations = updatedEvents.Where(item => item.ProcessingState == "Pending"
                    && item.ProcessingComment?.Contains("investigation", StringComparison.OrdinalIgnoreCase) == true).ToArray();
                operation = "save payment";
                await session.SavePaymentAsync(payment, token);
                // Release any previous applied identity before saving its replacement.
                foreach (var item in updatedEvents.OrderBy(item => item.ProcessingState == "Applied"))
                {
                    operation = "save payment event";
                    await session.SaveEventAsync(item, token);
                }
            }

            operation = "commit payment event transaction";
            await session.CommitAsync(token);
            foreach (var item in investigations)
                logger.LogWarning("Payment {PaymentId}, event {PaymentEventId} requires investigation after incoming event {IncomingPaymentEventId}: {Reason}",
                    paymentId, item.PaymentEventId, eventId, item.ProcessingComment);
            logger.LogInformation("Processed payment {PaymentId}, event {PaymentEventId}; state {PaymentState}",
                paymentId, eventId, payment?.State);
            return payment;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}",
                operation, paymentId, eventId);
            throw;
        }
    }
}

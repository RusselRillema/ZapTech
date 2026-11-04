namespace PaymentIntegration.Payments;

public sealed class PaymentEventService(IPaymentRepository repository,
    PaymentResponseProcessor processor, TimeProvider clock, ILogger<PaymentEventService> logger)
{
    public async Task<Payment?> ProcessAsync(Guid? paymentId, Guid eventId,
        PaymentEvent? receipt, bool retryingRateLimit = false)
    {
        // Finish auditing a received event even if its HTTP caller disconnects.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30), clock);
        var token = timeout.Token;
        var operation = "payment row lock";

        try
        {
            await using var session = await repository.BeginProcessingAsync(paymentId, eventId, token);
            var payment = session.Payment;
            if (payment is null && receipt?.Source != "PaymentAdvice")
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
                operation = "save unlinked payment advice";
                await session.SaveEventAsync(processor.ProcessUnlinkedAdvice(receipt!, clock.GetUtcNow()), token);
            }
            else
            {
                operation = "read pending payment events";
                var pending = (await session.GetPendingEventsAsync(token)).ToArray();
                bool applied;
                do
                {
                    applied = false;
                    for (var index = 0; index < pending.Length; index++)
                    {
                        if (pending[index].ProcessingState != "Pending")
                            continue;

                        operation = "process payment event";
                        // Earlier E29 receipts await the current attempt's outcome.
                        var deferRateLimitFailure = pending[index].PaymentEventId != eventId || retryingRateLimit;
                        var result = processor.Process(payment, pending[index], clock.GetUtcNow(), deferRateLimitFailure);
                        payment = result.Payment;
                        pending[index] = result.Event;
                        applied |= result.Event.ProcessingState == "Applied";
                    }
                } while (applied);

                payment = payment with { HasPendingEvents = pending.Any(item => item.ProcessingState == "Pending") };
                operation = "save payment";
                await session.SavePaymentAsync(payment, token);
                foreach (var item in pending)
                {
                    operation = "save payment event";
                    await session.SaveEventAsync(item, token);
                }
            }

            operation = "commit payment event transaction";
            await session.CommitAsync(token);
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

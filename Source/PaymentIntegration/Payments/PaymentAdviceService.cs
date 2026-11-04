using System.Text.Json;
using PaymentIntegration.Acme;

namespace PaymentIntegration.Payments;

public sealed class PaymentAdviceService(AcmeAdviceSettings settings, PaymentEventService events,
    TimeProvider clock, ILogger<PaymentAdviceService> logger)
{
    public async Task ReceiveAsync(JsonElement payload)
    {
        var transactionId = PaymentAdviceValidation.Validate(payload, settings);
        var eventId = Guid.NewGuid();
        Guid? paymentId = Guid.TryParse(transactionId, out var id) ? id : null;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["AcmeTransactionId"] = transactionId,
            ["PaymentEventId"] = eventId
        });
        try
        {
            await events.ProcessAsync(paymentId, eventId,
                new(eventId, paymentId, "PaymentAdvice", payload.Clone(), null, clock.GetUtcNow()));
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Payment Advice persistence failed for transaction {AcmeTransactionId}, event {PaymentEventId}",
                transactionId, eventId);
            throw;
        }
    }
}

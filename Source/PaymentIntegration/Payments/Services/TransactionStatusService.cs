using System.Text.Json;
using PaymentIntegration.Payments.Validation;

namespace PaymentIntegration.Payments.Services;

public sealed class TransactionStatusService(PaymentEventService events, TimeProvider clock,
    ILogger<TransactionStatusService> logger)
{
    public async Task ReceiveAsync(JsonElement payload)
    {
        var rrn = TransactionStatusValidation.Validate(payload);
        var eventId = Guid.NewGuid();
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["AcmeEventId"] = payload.GetProperty("transactionUUID").GetString()!,
            ["RetrievalReferenceNumber"] = rrn,
            ["PaymentEventId"] = eventId
        });
        await events.ProcessStatusAsync(rrn, eventId,
            new(eventId, null, "TransactionStatus", payload.Clone(), null, clock.GetUtcNow()));
    }
}

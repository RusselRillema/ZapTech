using System.Text.Json;

namespace PaymentIntegration.Payments.Models;

public sealed record PaymentEvent(Guid PaymentEventId, Guid? PaymentId, string Source,
    JsonElement? Payload, short? HttpStatusCode, DateTimeOffset ReceivedAt,
    string ProcessingState = "Pending", string? ProcessingComment = null,
    DateTimeOffset? ProcessedAt = null);

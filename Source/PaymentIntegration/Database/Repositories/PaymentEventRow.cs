using System.Text.Json;
using NpgsqlTypes;
using PaymentIntegration.Payments.Models;

namespace PaymentIntegration.Database.Repositories;

// Maps the explicit PostgreSQL result type, independently of the table's shape.
internal sealed class PaymentEventRow
{
    public PaymentEventRow()
    {
    }

    [PgName("payment_event_id")]
    public Guid PaymentEventId { get; set; }

    [PgName("payment_id")]
    public Guid? PaymentId { get; set; }

    [PgName("source")]
    public string Source { get; set; } = string.Empty;

    [PgName("payload")]
    public JsonElement? Payload { get; set; }

    [PgName("http_status_code")]
    public short? HttpStatusCode { get; set; }

    [PgName("received_at")]
    public DateTimeOffset ReceivedAt { get; set; }

    [PgName("processing_state")]
    public string ProcessingState { get; set; } = string.Empty;

    [PgName("processing_comment")]
    public string? ProcessingComment { get; set; }

    [PgName("processed_at")]
    public DateTimeOffset? ProcessedAt { get; set; }

    public PaymentEvent ToPaymentEvent()
    {
        return new PaymentEvent(
            PaymentEventId,
            PaymentId,
            Source,
            Payload,
            HttpStatusCode,
            ReceivedAt,
            ProcessingState,
            ProcessingComment,
            ProcessedAt);
    }
}

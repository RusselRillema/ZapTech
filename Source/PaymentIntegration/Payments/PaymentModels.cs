using System.Text.Json;

namespace PaymentIntegration.Payments;

public sealed record NewPaymentRequest(
    string? AppPaymentId, string? CustomerId, Guid CustomerCardId,
    string? SubMerchantId, long Amount, string? CurrencyCode);

public sealed record Payment(
    Guid PaymentId, string AppPaymentId, string CustomerId, Guid CustomerCardId,
    string SubMerchantId, string OrderNumber, string CurrencyCode, long RequestedAmount,
    string State, DateTimeOffset CreatedAt, long? ApprovedAmount = null,
    long? RemainingAmount = null, bool HasPendingEvents = false);

public sealed record PaymentDetails(string AcmeCardToken, string Email, string TradingName,
    string City, string RegionCode, string CountryCode, string MerchantCategoryCode);

public sealed record PaymentCreation(bool Created, Payment Payment);

public sealed record PaymentEvent(Guid PaymentEventId, Guid PaymentId, string Source,
    JsonElement? Payload, short? HttpStatusCode, DateTimeOffset ReceivedAt,
    string ProcessingState = "Pending", string? ProcessingComment = null,
    DateTimeOffset? ProcessedAt = null);

public sealed record PaymentResult(Guid PaymentId, string AppPaymentId, string State,
    string CurrencyCode, long RequestedAmount, long? ApprovedAmount, long? RemainingAmount,
    bool IsFullyReconciled);

public sealed class PaymentRequestException(int statusCode, string code, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

namespace PaymentIntegration.Payments.Models;

public sealed record NewPaymentRequest(
    string? AppPaymentId, string? CustomerId, Guid CustomerCardId,
    string? SubMerchantId, long Amount, string? CurrencyCode);

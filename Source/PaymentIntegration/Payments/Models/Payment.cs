namespace PaymentIntegration.Payments.Models;

public sealed record Payment(
    Guid PaymentId, string AppPaymentId, string CustomerId, Guid CustomerCardId,
    string SubMerchantId, string OrderNumber, string CurrencyCode, long RequestedAmount,
    string State, DateTimeOffset CreatedAt, long? ApprovedAmount = null,
    long? RemainingAmount = null, bool HasPendingEvents = false)
{
    public bool HasApproval => State is "Approved" or "PartiallyRefunded" or "Refunded" or "Reversed";
}

using NpgsqlTypes;
using PaymentIntegration.Payments;

namespace PaymentIntegration.Reports;

// Only original payment fields and raw receipts cross this read boundary.
internal sealed class PaymentHistoryRow
{
    [PgName("payment_id")]
    public Guid PaymentId { get; set; }
    [PgName("app_payment_id")]
    public string AppPaymentId { get; set; } = string.Empty;
    [PgName("customer_id")]
    public string CustomerId { get; set; } = string.Empty;
    [PgName("customer_card_id")]
    public Guid CustomerCardId { get; set; }
    [PgName("sub_merchant_id")]
    public string SubMerchantId { get; set; } = string.Empty;
    [PgName("sub_merchant_name")]
    public string SubMerchantName { get; set; } = string.Empty;
    [PgName("order_number")]
    public string OrderNumber { get; set; } = string.Empty;
    [PgName("currency_code")]
    public string CurrencyCode { get; set; } = string.Empty;
    [PgName("requested_amount")]
    public long RequestedAmount { get; set; }
    [PgName("created_at")]
    public DateTimeOffset CreatedAt { get; set; }
    [PgName("events")]
    public PaymentEventRow[] Events { get; set; } = [];

    public PaymentReportHistory ToHistory() => new(
        new Payment(PaymentId, AppPaymentId, CustomerId, CustomerCardId, SubMerchantId,
            OrderNumber, CurrencyCode, RequestedAmount, "Unresolved", CreatedAt),
        SubMerchantName, Events.Select(item => item.ToPaymentEvent()).ToArray());
}

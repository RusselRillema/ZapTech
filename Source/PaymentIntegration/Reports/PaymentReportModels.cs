using PaymentIntegration.Payments.Models;

namespace PaymentIntegration.Reports;

public sealed record PaymentReportHistory(Payment Payment, string SubMerchantName, IReadOnlyList<PaymentEvent> Events);

public sealed record DailyPaymentReport(DateOnly Date, DateTimeOffset GeneratedAt,
    IReadOnlyList<MerchantPaymentSummary> Summaries, IReadOnlyList<CurrencyPaymentSummary> CurrencySummaries);

public sealed record MerchantPaymentSummary : PaymentSummaryTotals
{
    public required string SubMerchantId { get; init; }
    public required string SubMerchantName { get; init; }
    public required string CurrencyCode { get; init; }
    public bool HasSubsequentActivity { get; init; }
}

public sealed record CurrencyPaymentSummary : PaymentSummaryTotals
{
    public required string CurrencyCode { get; init; }
    public int SubMerchantCount { get; init; }
    public int FlaggedSubMerchantCount { get; init; }
}

public record PaymentSummaryTotals
{
    public long PaymentsRequested { get; init; }
    public decimal TotalRequested { get; init; }
    public long PaymentsApproved { get; init; }
    public decimal TotalApproved { get; init; }
    public long PaymentsDeclined { get; init; }
    public decimal TotalDeclined { get; init; }
    public long Reversals { get; init; }
    public decimal TotalReversed { get; init; }
    public long Refunds { get; init; }
    public decimal TotalRefunded { get; init; }
    public decimal RemainingPaymentValue { get; init; }
    public long UnknownRemainingPayments { get; init; }
    public decimal PendingApprovalValue { get; init; }
}

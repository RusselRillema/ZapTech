using PaymentIntegration.Payments;

namespace PaymentIntegration.Reports;

public sealed class PaymentReportService(IPaymentReportRepository repository,
    PaymentHistoryReconstructor reconstructor, TimeProvider clock)
{
    public async Task<DailyPaymentReport> GetDailyAsync(DateOnly date, CancellationToken cancellationToken)
    {
        var period = ReportingPeriod.ForDate(date);
        var histories = await repository.GetHistoryAsync(period.End, cancellationToken);
        var reconstructed = new List<(PaymentReportHistory History, HistoricalPayment Historical)>();
        foreach (var history in histories.Where(item => item.Payment.CreatedAt < period.End))
        {
            cancellationToken.ThrowIfCancellationRequested();
            reconstructed.Add((history, reconstructor.Reconstruct(history, period, cancellationToken)));
        }

        var summaries = new List<MerchantPaymentSummary>();
        foreach (var group in reconstructed.GroupBy(item => (item.History.Payment.SubMerchantId, item.History.Payment.CurrencyCode))
                     .OrderBy(item => item.Key.SubMerchantId, StringComparer.Ordinal)
                     .ThenBy(item => item.Key.CurrencyCode, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var requested = group.Where(item => period.Contains(item.History.Payment.CreatedAt)).ToArray();
            var dailyOutcomes = group.Where(item => item.Historical.OutcomeAt is { } time && period.Contains(time)).ToArray();
            var approved = dailyOutcomes.Where(item => item.Historical.Payment.HasApproval).ToArray();
            var declined = dailyOutcomes.Where(item => item.Historical.Payment.State == "Declined").ToArray();
            var adjustments = group.SelectMany(item => item.Historical.Events)
                .Where(item => item.Source == "TransactionStatus" && item.ProcessingState == "Applied"
                    && period.Contains(ReportingPeriod.EffectiveAt(item))).ToArray();
            var reversals = adjustments.Where(item => item.Payload!.Value.GetProperty("transactionType").GetString() == "Reversal").ToArray();
            var refunds = adjustments.Where(item => item.Payload!.Value.GetProperty("transactionType").GetString() == "Refund").ToArray();

            summaries.Add(new MerchantPaymentSummary
            {
                SubMerchantId = group.Key.SubMerchantId,
                SubMerchantName = group.First().History.SubMerchantName,
                CurrencyCode = group.Key.CurrencyCode,
                PaymentsRequested = requested.LongLength,
                TotalRequested = requested.Sum(item => (decimal)item.History.Payment.RequestedAmount),
                PaymentsApproved = approved.LongLength,
                TotalApproved = approved.Sum(item => (decimal)(item.Historical.Payment.ApprovedAmount
                    ?? throw new InvalidOperationException("An approved payment has no authoritative approved amount."))),
                PaymentsDeclined = declined.LongLength,
                TotalDeclined = declined.Sum(item => (decimal)item.History.Payment.RequestedAmount),
                Reversals = reversals.LongLength,
                TotalReversed = reversals.Sum(item => (decimal)item.Payload!.Value.GetProperty("transactionAmount").GetInt64()),
                Refunds = refunds.LongLength,
                TotalRefunded = refunds.Sum(item => (decimal)item.Payload!.Value.GetProperty("transactionAmount").GetInt64()),
                RemainingPaymentValue = group.Sum(item => (decimal)(item.Historical.Payment.RemainingAmount ?? 0)),
                UnknownRemainingPayments = group.LongCount(item => item.Historical.Payment.HasApproval
                    && item.Historical.Payment.RemainingAmount is null),
                PendingApprovalValue = group.Where(item => item.Historical.Payment.State == "Unresolved")
                    .Sum(item => (decimal)item.History.Payment.RequestedAmount),
                HasSubsequentActivity = group.Any(item => item.Historical.HasSubsequentActivity)
            });
        }
        return new(date, clock.GetUtcNow(), summaries);
    }

}

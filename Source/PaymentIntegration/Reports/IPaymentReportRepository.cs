namespace PaymentIntegration.Reports;

public interface IPaymentReportRepository
{
    Task<IReadOnlyList<PaymentReportHistory>> GetHistoryAsync(
        DateTimeOffset createdBefore, CancellationToken cancellationToken);
}

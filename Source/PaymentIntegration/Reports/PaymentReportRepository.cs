using Npgsql;
using NpgsqlTypes;

namespace PaymentIntegration.Reports;

public sealed class PaymentReportRepository(NpgsqlDataSource dataSource, ILogger<PaymentReportRepository> logger)
    : IPaymentReportRepository
{
    public async Task<IReadOnlyList<PaymentReportHistory>> GetHistoryAsync(
        DateTimeOffset createdBefore, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(
                "CALL public.payments_get_report_history(p_created_before => @p_created_before, p_result_histories => NULL)",
                connection) { CommandTimeout = 30 };
            command.Parameters.Add(new NpgsqlParameter("p_created_before", NpgsqlDbType.TimestampTz)
            {
                Value = createdBefore.ToUniversalTime()
            });
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
                throw new InvalidOperationException("Stored procedure payments_get_report_history returned no output row.");

            var rows = reader.GetFieldValue<PaymentHistoryRow[]>(reader.GetOrdinal("p_result_histories"));
            return rows.Select(item => item.ToHistory()).ToArray();
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database operation {Operation} failed for report cutoff {ReportCutoff}",
                "payments_get_report_history", createdBefore);
            throw;
        }
    }
}

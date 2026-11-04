using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace PaymentIntegration.Payments;

public sealed class PaymentRepository(NpgsqlDataSource dataSource, ILogger<PaymentRepository> logger)
    : IPaymentRepository
{
    private readonly record struct Argument(string Name, NpgsqlDbType Type, object? Value);

    public Task<Payment?> GetByAppIdAsync(string appPaymentId, CancellationToken cancellationToken) =>
        ReadAsync<Payment>("payments_get_by_app_id",
            [new("p_app_payment_id", NpgsqlDbType.Text, appPaymentId)], cancellationToken, appPaymentId);

    public Task<PaymentDetails?> GetDetailsAsync(string customerId, Guid customerCardId,
        string subMerchantId, CancellationToken cancellationToken) =>
        ReadAsync<PaymentDetails>("payment_details_get",
            [new("p_customer_id", NpgsqlDbType.Text, customerId),
             new("p_customer_card_id", NpgsqlDbType.Uuid, customerCardId),
             new("p_sub_merchant_id", NpgsqlDbType.Text, subMerchantId)], cancellationToken);

    public async Task<PaymentCreation> CreateAsync(Payment payment, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            var result = await CallAsync<PaymentCreation>(connection, transaction, "payments_create",
                [new("p_payment_id", NpgsqlDbType.Uuid, payment.PaymentId),
                 new("p_app_payment_id", NpgsqlDbType.Text, payment.AppPaymentId),
                 new("p_customer_id", NpgsqlDbType.Text, payment.CustomerId),
                 new("p_customer_card_id", NpgsqlDbType.Uuid, payment.CustomerCardId),
                 new("p_sub_merchant_id", NpgsqlDbType.Text, payment.SubMerchantId),
                 new("p_order_number", NpgsqlDbType.Text, payment.OrderNumber),
                 new("p_currency_code", NpgsqlDbType.Text, payment.CurrencyCode),
                 new("p_requested_amount", NpgsqlDbType.Bigint, payment.RequestedAmount),
                 new("p_state", NpgsqlDbType.Text, payment.State),
                 new("p_created_at", NpgsqlDbType.TimestampTz, payment.CreatedAt)], cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            logger.LogInformation("Saved payment {PaymentId}; created {Created}", result!.Payment.PaymentId, result.Created);
            return result;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database operation {Operation} failed for payment {PaymentId}",
                "payments_create/commit", payment.PaymentId);
            throw;
        }
    }

    public async Task<IPaymentProcessingSession> BeginProcessingAsync(Guid paymentId, Guid eventId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;
        try
        {
            connection = await dataSource.OpenConnectionAsync(cancellationToken);
            transaction = await connection.BeginTransactionAsync(cancellationToken);
            var payment = await CallAsync<Payment>(connection, transaction, "payments_get_for_update",
                [new("p_payment_id", NpgsqlDbType.Uuid, paymentId)], cancellationToken);
            return new Session(connection, transaction, payment!, eventId, logger);
        }
        catch (Exception exception)
        {
            logger.LogError(exception,
                "Database operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}",
                "payments_get_for_update", paymentId, eventId);
            try
            {
                if (transaction is not null) await transaction.DisposeAsync();
            }
            finally
            {
                if (connection is not null) await connection.DisposeAsync();
            }
            throw;
        }
    }

    private async Task<T?> ReadAsync<T>(string procedure, Argument[] arguments, CancellationToken cancellationToken,
        string? appPaymentId = null)
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            return await CallAsync<T>(connection, null, procedure, arguments, cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Database operation {Operation} failed for app payment {AppPaymentId}", procedure, appPaymentId);
            throw;
        }
    }

    // Names are internal constants. All values are bound, including nullable values.
    private static NpgsqlCommand Command(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string procedure, Argument[] arguments, bool returnsResult)
    {
        var names = arguments.Select(argument => $"{argument.Name} => @{argument.Name}").ToList();
        if (returnsResult) names.Add("p_result => NULL");
        var command = new NpgsqlCommand($"CALL public.{procedure}({string.Join(", ", names)})", connection, transaction)
        {
            CommandTimeout = 15
        };
        foreach (var argument in arguments)
            command.Parameters.Add(new NpgsqlParameter(argument.Name, argument.Type) { Value = argument.Value ?? DBNull.Value });
        return command;
    }

    private static async Task<T?> CallAsync<T>(NpgsqlConnection connection, NpgsqlTransaction? transaction,
        string procedure, Argument[] arguments, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, transaction, procedure, arguments, true);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is null or DBNull ? default : JsonSerializer.Deserialize<T>((string)result);
    }

    private sealed class Session(NpgsqlConnection connection, NpgsqlTransaction transaction,
        Payment payment, Guid eventId, ILogger<PaymentRepository> logger) : IPaymentProcessingSession
    {
        private bool committed;
        public Payment Payment { get; } = payment;

        public Task InsertEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken) =>
            ExecuteAsync("payment_events_create",
                [new("p_payment_event_id", NpgsqlDbType.Uuid, paymentEvent.PaymentEventId),
                 new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId),
                 new("p_source", NpgsqlDbType.Text, paymentEvent.Source),
                 new("p_payload", NpgsqlDbType.Jsonb, paymentEvent.Payload?.GetRawText()),
                 new("p_http_status_code", NpgsqlDbType.Smallint, paymentEvent.HttpStatusCode),
                 new("p_received_at", NpgsqlDbType.TimestampTz, paymentEvent.ReceivedAt)], cancellationToken);

        public async Task<IReadOnlyList<PaymentEvent>> GetPendingEventsAsync(CancellationToken cancellationToken)
        {
            try
            {
                return (await CallAsync<List<PaymentEvent>>(connection, transaction, "payment_events_get_pending",
                    [new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId)], cancellationToken))!;
            }
            catch (Exception exception)
            {
                LogFailure(exception, "payment_events_get_pending");
                throw;
            }
        }

        public Task SavePaymentAsync(Payment updated, CancellationToken cancellationToken) =>
            ExecuteAsync("payments_update",
                [new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId),
                 new("p_state", NpgsqlDbType.Text, updated.State),
                 new("p_approved_amount", NpgsqlDbType.Bigint, updated.ApprovedAmount),
                 new("p_remaining_amount", NpgsqlDbType.Bigint, updated.RemainingAmount)], cancellationToken);

        public Task SaveEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken) =>
            ExecuteAsync("payment_events_update",
                [new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId),
                 new("p_payment_event_id", NpgsqlDbType.Uuid, paymentEvent.PaymentEventId),
                 new("p_processing_state", NpgsqlDbType.Text, paymentEvent.ProcessingState),
                 new("p_processing_comment", NpgsqlDbType.Text, paymentEvent.ProcessingComment),
                 new("p_processed_at", NpgsqlDbType.TimestampTz, paymentEvent.ProcessedAt)], cancellationToken);

        public async Task CommitAsync(CancellationToken cancellationToken)
        {
            try
            {
                await transaction.CommitAsync(cancellationToken);
                committed = true;
            }
            catch (Exception exception)
            {
                LogFailure(exception, "commit");
                throw;
            }
        }

        private async Task ExecuteAsync(string procedure, Argument[] arguments, CancellationToken cancellationToken)
        {
            try
            {
                await using var command = Command(connection, transaction, procedure, arguments, false);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                LogFailure(exception, procedure);
                throw;
            }
        }

        private void LogFailure(Exception exception, string operation) => logger.LogError(exception,
            "Database operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}",
            operation, Payment.PaymentId, eventId);

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!committed)
                {
                    try
                    {
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        await transaction.RollbackAsync(timeout.Token);
                        logger.LogWarning("Rolled back payment {PaymentId}, event {PaymentEventId}", Payment.PaymentId, eventId);
                    }
                    catch (Exception exception) { LogFailure(exception, "rollback"); }
                }
            }
            finally
            {
                try { await transaction.DisposeAsync(); }
                finally { await connection.DisposeAsync(); }
            }
        }
    }
}

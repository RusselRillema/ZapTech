using Npgsql;
using NpgsqlTypes;

namespace PaymentIntegration.Payments;

public sealed class PaymentRepository(NpgsqlDataSource dataSource, ILogger<PaymentRepository> logger)
    : IPaymentRepository
{
    private readonly record struct Argument(string Name, NpgsqlDbType Type, object? Value);
    private sealed record CreatedPayment(Guid PaymentId, bool Created);

    private readonly string[] paymentOutputNames =
    [
        "p_result_payment_id",
        "p_result_app_payment_id",
        "p_result_customer_id",
        "p_result_customer_card_id",
        "p_result_sub_merchant_id",
        "p_result_order_number",
        "p_result_currency_code",
        "p_result_requested_amount",
        "p_result_approved_amount",
        "p_result_remaining_amount",
        "p_result_state",
        "p_result_created_at",
        "p_result_has_pending_events"
    ];

    private readonly string[] detailsOutputNames =
    [
        "p_result_acme_card_token",
        "p_result_email",
        "p_result_trading_name",
        "p_result_city",
        "p_result_region_code",
        "p_result_country_code",
        "p_result_merchant_category_code"
    ];

    public Task<Payment?> GetByAppIdAsync(
        string appPaymentId,
        CancellationToken cancellationToken)
    {
        return ReadAsync(
            "payments_get_by_app_id",
            [new("p_app_payment_id", NpgsqlDbType.Text, appPaymentId)],
            paymentOutputNames,
            ReadPayment,
            cancellationToken,
            appPaymentId);
    }

    public Task<PaymentDetails?> GetDetailsAsync(
        string customerId,
        Guid customerCardId,
        string subMerchantId,
        CancellationToken cancellationToken)
    {
        return ReadAsync(
            "payment_details_get",
            [
                new("p_customer_id", NpgsqlDbType.Text, customerId),
                new("p_customer_card_id", NpgsqlDbType.Uuid, customerCardId),
                new("p_sub_merchant_id", NpgsqlDbType.Text, subMerchantId)
            ],
            detailsOutputNames,
            ReadDetails,
            cancellationToken);
    }

    public async Task<PaymentCreation> CreateAsync(
        Payment payment,
        CancellationToken cancellationToken)
    {
        var operation = "payments_create";

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var created = await CallAsync(
                connection,
                transaction,
                "payments_create",
                [
                    new("p_payment_id", NpgsqlDbType.Uuid, payment.PaymentId),
                    new("p_app_payment_id", NpgsqlDbType.Text, payment.AppPaymentId),
                    new("p_customer_id", NpgsqlDbType.Text, payment.CustomerId),
                    new("p_customer_card_id", NpgsqlDbType.Uuid, payment.CustomerCardId),
                    new("p_sub_merchant_id", NpgsqlDbType.Text, payment.SubMerchantId),
                    new("p_order_number", NpgsqlDbType.Text, payment.OrderNumber),
                    new("p_currency_code", NpgsqlDbType.Text, payment.CurrencyCode),
                    new("p_requested_amount", NpgsqlDbType.Bigint, payment.RequestedAmount),
                    new("p_state", NpgsqlDbType.Text, payment.State),
                    new("p_created_at", NpgsqlDbType.TimestampTz, payment.CreatedAt)
                ],
                ["p_result_created", "p_result_payment_id"],
                reader => new CreatedPayment(
                    ReadValue<Guid>(reader, "p_result_payment_id"),
                    ReadValue<bool>(reader, "p_result_created")),
                cancellationToken);

            operation = "payments_get_for_update";
            var persisted = await GetLockedPaymentAsync(
                connection,
                transaction,
                created.PaymentId,
                cancellationToken);

            operation = "commit";
            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "Saved payment {PaymentId}; created {Created}",
                persisted.PaymentId,
                created.Created);

            return new PaymentCreation(created.Created, persisted);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Database operation {Operation} failed for payment {PaymentId}",
                operation,
                payment.PaymentId);
            throw;
        }
    }

    public async Task<IPaymentProcessingSession> BeginProcessingAsync(
        Guid paymentId,
        Guid eventId,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection? connection = null;
        NpgsqlTransaction? transaction = null;

        try
        {
            connection = await dataSource.OpenConnectionAsync(cancellationToken);
            transaction = await connection.BeginTransactionAsync(cancellationToken);

            var payment = await GetLockedPaymentAsync(
                connection,
                transaction,
                paymentId,
                cancellationToken);

            return new Session(connection, transaction, payment, eventId, this, logger);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Database operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}",
                "payments_get_for_update",
                paymentId,
                eventId);

            try
            {
                if (transaction is not null)
                {
                    await transaction.DisposeAsync();
                }
            }
            finally
            {
                if (connection is not null)
                {
                    await connection.DisposeAsync();
                }
            }

            throw;
        }
    }

    private async Task<Payment> GetLockedPaymentAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid paymentId,
        CancellationToken cancellationToken)
    {
        var payment = await CallAsync(
            connection,
            transaction,
            "payments_get_for_update",
            [new("p_payment_id", NpgsqlDbType.Uuid, paymentId)],
            paymentOutputNames,
            ReadPayment,
            cancellationToken);

        return payment ?? throw new InvalidOperationException(
            "The locked payment procedure returned no payment.");
    }

    private async Task<T?> ReadAsync<T>(
        string procedure,
        Argument[] arguments,
        IReadOnlyList<string> outputNames,
        Func<NpgsqlDataReader, T?> readResult,
        CancellationToken cancellationToken,
        string? appPaymentId = null)
        where T : class
    {
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);

            return await CallAsync(
                connection,
                null,
                procedure,
                arguments,
                outputNames,
                readResult,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(
                exception,
                "Database operation {Operation} failed for app payment {AppPaymentId}",
                procedure,
                appPaymentId);
            throw;
        }
    }

    // Procedure and parameter names are internal constants. Every input value is bound.
    private NpgsqlCommand Command(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string procedure,
        Argument[] arguments,
        IReadOnlyList<string> outputNames)
    {
        var parameters = arguments
            .Select(argument => $"{argument.Name} => @{argument.Name}")
            .Concat(outputNames.Select(name => $"{name} => NULL"));

        var command = new NpgsqlCommand(
            $"CALL public.{procedure}({string.Join(", ", parameters)})",
            connection,
            transaction)
        {
            CommandTimeout = 15
        };

        foreach (var argument in arguments)
        {
            command.Parameters.Add(new NpgsqlParameter(argument.Name, argument.Type)
            {
                Value = argument.Value ?? DBNull.Value
            });
        }

        return command;
    }

    private async Task<T> CallAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        string procedure,
        Argument[] arguments,
        IReadOnlyList<string> outputNames,
        Func<NpgsqlDataReader, T> readResult,
        CancellationToken cancellationToken)
    {
        await using var command = Command(
            connection,
            transaction,
            procedure,
            arguments,
            outputNames);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new InvalidOperationException(
                $"Stored procedure {procedure} returned no output row.");
        }

        return readResult(reader);
    }

    private Payment? ReadPayment(NpgsqlDataReader reader)
    {
        if (reader.IsDBNull(reader.GetOrdinal("p_result_payment_id")))
        {
            return null;
        }

        return new Payment(
            ReadValue<Guid>(reader, "p_result_payment_id"),
            ReadValue<string>(reader, "p_result_app_payment_id"),
            ReadValue<string>(reader, "p_result_customer_id"),
            ReadValue<Guid>(reader, "p_result_customer_card_id"),
            ReadValue<string>(reader, "p_result_sub_merchant_id"),
            ReadValue<string>(reader, "p_result_order_number"),
            ReadValue<string>(reader, "p_result_currency_code"),
            ReadValue<long>(reader, "p_result_requested_amount"),
            ReadValue<string>(reader, "p_result_state"),
            ReadValue<DateTimeOffset>(reader, "p_result_created_at"),
            ReadNullableValue<long>(reader, "p_result_approved_amount"),
            ReadNullableValue<long>(reader, "p_result_remaining_amount"),
            ReadValue<bool>(reader, "p_result_has_pending_events"));
    }

    private PaymentDetails? ReadDetails(NpgsqlDataReader reader)
    {
        if (reader.IsDBNull(reader.GetOrdinal("p_result_acme_card_token")))
        {
            return null;
        }

        return new PaymentDetails(
            ReadValue<string>(reader, "p_result_acme_card_token"),
            ReadValue<string>(reader, "p_result_email"),
            ReadValue<string>(reader, "p_result_trading_name"),
            ReadValue<string>(reader, "p_result_city"),
            ReadValue<string>(reader, "p_result_region_code"),
            ReadValue<string>(reader, "p_result_country_code"),
            ReadValue<string>(reader, "p_result_merchant_category_code"));
    }

    private IReadOnlyList<PaymentEvent> ReadPendingEvents(NpgsqlDataReader reader)
    {
        var rows = ReadValue<PaymentEventRow[]>(reader, "p_result_events");
        return rows.Select(row => row.ToPaymentEvent()).ToArray();
    }

    private T ReadValue<T>(NpgsqlDataReader reader, string name)
    {
        return reader.GetFieldValue<T>(reader.GetOrdinal(name));
    }

    private T? ReadNullableValue<T>(NpgsqlDataReader reader, string name)
        where T : struct
    {
        var ordinal = reader.GetOrdinal(name);

        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return reader.GetFieldValue<T>(ordinal);
    }

    private sealed class Session(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Payment payment,
        Guid eventId,
        PaymentRepository repository,
        ILogger<PaymentRepository> logger) : IPaymentProcessingSession
    {
        private bool committed;

        public Payment Payment { get; } = payment;

        public Task InsertEventAsync(
            PaymentEvent paymentEvent,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                "payment_events_create",
                [
                    new("p_payment_event_id", NpgsqlDbType.Uuid, paymentEvent.PaymentEventId),
                    new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId),
                    new("p_source", NpgsqlDbType.Text, paymentEvent.Source),
                    new("p_payload", NpgsqlDbType.Jsonb, paymentEvent.Payload?.GetRawText()),
                    new("p_http_status_code", NpgsqlDbType.Smallint, paymentEvent.HttpStatusCode),
                    new("p_received_at", NpgsqlDbType.TimestampTz, paymentEvent.ReceivedAt)
                ],
                cancellationToken);
        }

        public async Task<IReadOnlyList<PaymentEvent>> GetPendingEventsAsync(
            CancellationToken cancellationToken)
        {
            try
            {
                return await repository.CallAsync(
                    connection,
                    transaction,
                    "payment_events_get_pending",
                    [new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId)],
                    ["p_result_events"],
                    repository.ReadPendingEvents,
                    cancellationToken);
            }
            catch (Exception exception)
            {
                LogFailure(exception, "payment_events_get_pending");
                throw;
            }
        }

        public Task SavePaymentAsync(
            Payment updated,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                "payments_update",
                [
                    new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId),
                    new("p_state", NpgsqlDbType.Text, updated.State),
                    new("p_approved_amount", NpgsqlDbType.Bigint, updated.ApprovedAmount),
                    new("p_remaining_amount", NpgsqlDbType.Bigint, updated.RemainingAmount)
                ],
                cancellationToken);
        }

        public Task SaveEventAsync(
            PaymentEvent paymentEvent,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(
                "payment_events_update",
                [
                    new("p_payment_id", NpgsqlDbType.Uuid, Payment.PaymentId),
                    new("p_payment_event_id", NpgsqlDbType.Uuid, paymentEvent.PaymentEventId),
                    new("p_processing_state", NpgsqlDbType.Text, paymentEvent.ProcessingState),
                    new("p_processing_comment", NpgsqlDbType.Text, paymentEvent.ProcessingComment),
                    new("p_processed_at", NpgsqlDbType.TimestampTz, paymentEvent.ProcessedAt)
                ],
                cancellationToken);
        }

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

        private async Task ExecuteAsync(
            string procedure,
            Argument[] arguments,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var command = repository.Command(
                    connection,
                    transaction,
                    procedure,
                    arguments,
                    []);

                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (Exception exception)
            {
                LogFailure(exception, procedure);
                throw;
            }
        }

        private void LogFailure(Exception exception, string operation)
        {
            logger.LogError(
                exception,
                "Database operation {Operation} failed for payment {PaymentId}, event {PaymentEventId}",
                operation,
                Payment.PaymentId,
                eventId);
        }

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

                        logger.LogWarning(
                            "Rolled back payment {PaymentId}, event {PaymentEventId}",
                            Payment.PaymentId,
                            eventId);
                    }
                    catch (Exception exception)
                    {
                        LogFailure(exception, "rollback");
                    }
                }
            }
            finally
            {
                try
                {
                    await transaction.DisposeAsync();
                }
                finally
                {
                    await connection.DisposeAsync();
                }
            }
        }
    }
}

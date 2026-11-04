namespace PaymentIntegration.Payments;

public interface IPaymentRepository
{
    Task<Payment?> GetByAppIdAsync(string appPaymentId, CancellationToken cancellationToken);
    Task<PaymentDetails?> GetDetailsAsync(string customerId, Guid customerCardId,
        string subMerchantId, CancellationToken cancellationToken);
    Task<PaymentCreation> CreateAsync(Payment payment, CancellationToken cancellationToken);
    Task<IPaymentProcessingSession> BeginProcessingAsync(Guid paymentId, Guid eventId,
        CancellationToken cancellationToken);
}

// Every operation on a session shares its locked payment, connection and transaction.
public interface IPaymentProcessingSession : IAsyncDisposable
{
    Payment Payment { get; }
    Task InsertEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentEvent>> GetPendingEventsAsync(CancellationToken cancellationToken);
    Task SavePaymentAsync(Payment payment, CancellationToken cancellationToken);
    Task SaveEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

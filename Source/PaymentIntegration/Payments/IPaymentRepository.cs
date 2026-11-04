namespace PaymentIntegration.Payments;

public interface IPaymentRepository
{
    Task<Payment?> GetByAppIdAsync(string appPaymentId, CancellationToken cancellationToken);
    Task<PaymentDetails?> GetDetailsAsync(string customerId, Guid customerCardId,
        string subMerchantId, CancellationToken cancellationToken);
    Task<PaymentCreation> CreateAsync(Payment payment, CancellationToken cancellationToken);
    Task<IPaymentProcessingSession> BeginProcessingAsync(Guid? paymentId, Guid eventId,
        CancellationToken cancellationToken);
}

// Operations within this processing session use one connection and transaction.
// Each container creates its own sessions; PostgreSQL coordinates them through payment row locks.
// For an existing payment, its row lock is held until this session's transaction ends.
public interface IPaymentProcessingSession : IAsyncDisposable
{
    Payment? Payment { get; }
    Task InsertEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentEvent>> GetPendingEventsAsync(CancellationToken cancellationToken);
    Task SavePaymentAsync(Payment payment, CancellationToken cancellationToken);
    Task SaveEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

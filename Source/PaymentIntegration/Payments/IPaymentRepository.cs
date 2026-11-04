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

// All operations share one connection and transaction; linked events retain the payment row lock.
public interface IPaymentProcessingSession : IAsyncDisposable
{
    Payment? Payment { get; }
    Task InsertEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken);
    Task<IReadOnlyList<PaymentEvent>> GetPendingEventsAsync(CancellationToken cancellationToken);
    Task SavePaymentAsync(Payment payment, CancellationToken cancellationToken);
    Task SaveEventAsync(PaymentEvent paymentEvent, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

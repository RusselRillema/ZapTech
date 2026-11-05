using PaymentIntegration.Acme.Contracts;

namespace PaymentIntegration.Acme.Client;

public interface IAcmePaymentClient
{
    Task<AcmeReply> SubmitAsync(AcmeEnvelope request, CancellationToken cancellationToken);
    Task<AcmeReply> QueryAsync(Guid paymentId, CancellationToken cancellationToken);
}

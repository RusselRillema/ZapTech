namespace PaymentIntegration.Payments;

public sealed class PaymentStatusService(IPaymentRepository repository)
{
    public async Task<PaymentResult> GetAsync(string? appPaymentId, CancellationToken cancellationToken)
    {
        PaymentValidation.ValidateAppPaymentId(appPaymentId);

        var payment = await repository.GetByAppIdAsync(appPaymentId!, cancellationToken);
        if (payment is null)
        {
            throw new PaymentRequestException(404, "payment_not_found",
                "No payment was found for the supplied AppPaymentId.");
        }

        return PaymentResult.FromPayment(payment);
    }
}

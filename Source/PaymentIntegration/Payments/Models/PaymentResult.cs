namespace PaymentIntegration.Payments.Models;

public sealed record PaymentResult(Guid PaymentId, string AppPaymentId, string State,
    string CurrencyCode, long RequestedAmount, long? ApprovedAmount, long? RemainingAmount,
    bool IsFullyReconciled)
{
    public static PaymentResult FromPayment(Payment payment) => new(payment.PaymentId, payment.AppPaymentId,
        payment.State, payment.CurrencyCode, payment.RequestedAmount, payment.ApprovedAmount, payment.RemainingAmount,
        (payment.HasApproval || payment.State is "Declined" or "Failed") && !payment.HasPendingEvents);
}

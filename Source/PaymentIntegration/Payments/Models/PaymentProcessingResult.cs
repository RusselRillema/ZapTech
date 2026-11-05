namespace PaymentIntegration.Payments.Models;

public sealed record PaymentProcessingResult(Payment Payment, IReadOnlyList<PaymentEvent> Events);

using PaymentIntegration.Payments;

namespace PaymentIntegration.Acme;

// A bank-specific implementation owns its statement descriptor requirements.
public interface ICardAcceptorNameFormatter
{
    string Format(PaymentDetails details);
}

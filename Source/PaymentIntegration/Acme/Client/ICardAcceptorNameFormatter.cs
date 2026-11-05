using PaymentIntegration.Payments.Models;

namespace PaymentIntegration.Acme.Client;

// A bank-specific implementation owns its statement descriptor requirements.
public interface ICardAcceptorNameFormatter
{
    string Format(PaymentDetails details);
}

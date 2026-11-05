namespace PaymentIntegration.Acme.Client;

public sealed class CardAcceptorNameFormatterFactory(FnbCardAcceptorNameFormatter formatter)
{
    public ICardAcceptorNameFormatter GetFormatter()
    {
        // Add an acquirerCode parameter when multiple acquiring banks need to be supported.
        return formatter;
    }
}

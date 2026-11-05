using PaymentIntegration.Payments.Models;

namespace PaymentIntegration.Acme.Client;

public sealed class FnbCardAcceptorNameFormatter : ICardAcceptorNameFormatter
{
    public string Format(PaymentDetails details)
    {
        var merchant = "ZAP*" + details.TradingName;
        if (string.IsNullOrWhiteSpace(details.TradingName) || merchant.Length > 23
            || string.IsNullOrWhiteSpace(details.City) || details.City.Length > 13
            || details.RegionCode.Length < 2 || details.CountryCode.Length != 2
            || details.TradingName.Contains('*') || details.City.Contains('*')
            || details.RegionCode[..2].Contains('*') || details.CountryCode.Contains('*'))
            throw new PaymentRequestException(422, "invalid_card_acceptor_name",
                "Configured sub-merchant statement details do not satisfy the acquiring bank format.");

        return merchant.PadRight(23) + details.City.PadRight(13)
            + details.RegionCode[..2] + details.CountryCode;
    }
}

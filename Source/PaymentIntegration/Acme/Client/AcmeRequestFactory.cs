using System.Globalization;
using PaymentIntegration.Acme.Contracts;
using PaymentIntegration.Payments.Models;

namespace PaymentIntegration.Acme.Client;

public sealed class AcmeRequestFactory(AcmeSettings settings, TimeProvider clock, CardAcceptorNameFormatterFactory formatterFactory)
{
    public AcmeEnvelope Create(Payment payment, PaymentDetails details) => new(
        new(clock.GetUtcNow().ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
            Guid.NewGuid().ToString(), "App"),
        new(settings.MerchantId, payment.PaymentId.ToString(), payment.OrderNumber, payment.RequestedAmount,
            payment.CurrencyCode, "CardNotPresent", new(details.AcmeCardToken), "CIT", payment.SubMerchantId,
            details.MerchantCategoryCode, formatterFactory.GetFormatter().Format(details), details.Email));
}

using System.Globalization;

namespace AcmePay.Stub;

public static class PaymentValidation
{
    public static string? Error(PaymentEnvelope? envelope, StubSettings settings)
    {
        var header = envelope?.MessageHeader;
        var payment = envelope?.PaymentRequest;
        if (header is null || payment is null)
            return "MessageHeader and PaymentRequest are required.";
        if (string.IsNullOrWhiteSpace(header.MessageID)
            || header.Channel is not ("App" or "Web" or "POS")
            || !DateTime.TryParseExact(header.MessageDateTime, "yyyy-MM-dd'T'HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return "MessageHeader is missing or invalid.";
        if (!Text(payment.MerchantID, 50) || !Guid.TryParse(payment.MerchantID, out var merchantId)
            || merchantId != settings.MerchantId)
            return "MerchantID must match the configured merchant.";
        if (!Text(payment.TransactionID, 50) || !Text(payment.OrderNumber, 50)
            || !payment.OrderNumber!.All(char.IsAsciiLetterOrDigit) || payment.Amount is null)
            return "Invalid payment IDs, OrderNumber or Amount.";
        if (payment.CurrencyCode is not null && (payment.CurrencyCode.Length != 3
            || !payment.CurrencyCode.All(c => c is >= 'A' and <= 'Z')))
            return "CurrencyCode requires three uppercase letters.";
        if (payment.PaymentService is not ("CardNotPresent" or "CardNotPresentRecurring" or "AcmeEft" or "GooglePay" or "ApplePay"))
            return "PaymentService is missing or invalid.";
        if ((payment.PaymentService != "AcmeEft" && string.IsNullOrWhiteSpace(payment.Card?.Token))
            || payment.Card?.ExtraFields is { Count: > 0 })
            return "Card.Token required; card details are forbidden.";
        if (payment.CardOnFileScenario is not (null or "CIT" or "MIT") || payment.Email?.Length > 254)
            return "CardOnFileScenario or Email is invalid.";
        if (!Text(payment.SubMerchantID, 15) || !Text(payment.MerchantCategoryCode, 4)
            || payment.CardAcceptorName is not { Length: 40 } name)
            return "Missing or invalid facilitator fields.";
        var asterisk = name.IndexOf('*');
        if (asterisk is not (3 or 7 or 12) || name.LastIndexOf('*') != asterisk)
            return "Invalid FNB CardAcceptorName asterisk position.";
        return null;
    }

    private static bool Text(string? value, int maximum) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= maximum;
}

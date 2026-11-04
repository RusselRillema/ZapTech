using System.Text.Json;
using PaymentIntegration.Acme;

namespace PaymentIntegration.Payments;

internal static class PaymentAdviceValidation
{
    public static string Validate(JsonElement payload, AcmeAdviceSettings settings)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw Invalid("Payment Advice must be a JSON object.");

        var requestType = Required(payload, "RequestType");
        if (requestType is not ("Payment" or "Authorize" or "Capture"))
            throw Invalid("RequestType is invalid.");
        if (!Guid.TryParse(Required(payload, "MerchantID", 50), out var merchantId)
            || merchantId != settings.MerchantId)
            throw Invalid("MerchantID does not identify the configured Acme account.");
        var transactionId = Required(payload, "TransactionID", 50);
        Required(payload, "RetrievalReferenceNumber", 12);
        Required(payload, "SystemsTraceAuditNumber", 6);
        Required(payload, "PaymentService");
        if (Required(payload, "TransactionStatus") is not ("Success" or "Failure" or "Other"))
            throw Invalid("TransactionStatus is invalid.");
        Required(payload, "ResponseSource", 50);
        Required(payload, "ResponseCode", 3);
        if (Required(payload, "CommProtocol", 4) != "REST")
            throw Invalid("CommProtocol must be REST.");
        if (!payload.TryGetProperty("Amount", out var amount)
            || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out _))
            throw Invalid("Amount must be an integer in the currency's lowest monetary unit.");

        Optional(payload, "OrderNumber", 50);
        Optional(payload, "ReconID", 50);
        Optional(payload, "ResponseDescription", 50);
        Optional(payload, "AuthCode", 6);
        Optional(payload, "MaskedCardNumber", 19);
        if (Optional(payload, "CurrencyCode", 3) is { } currency
            && (currency.Length != 3 || currency.Any(character => character is < 'A' or > 'Z')))
            throw Invalid("CurrencyCode must contain three uppercase letters.");
        if (Optional(payload, "TransactionDateTime") is { } timestamp
            && !DateTime.TryParseExact(timestamp, "yyyy-MM-dd'T'HH:mm:ss.fff",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out _))
            throw Invalid("TransactionDateTime must use Acme's SAST timestamp format.");
        return transactionId;
    }

    private static string Required(JsonElement payload, string name, int max = int.MaxValue) =>
        Optional(payload, name, max) is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text)
            ? text : throw Invalid($"{name} is required.");

    private static string? Optional(JsonElement payload, string name, int max = int.MaxValue)
    {
        if (!payload.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null)
            return null;
        if (value.ValueKind != JsonValueKind.String || value.GetString()!.Length > max)
            throw Invalid($"{name} must be a string of at most {max} characters.");
        return value.GetString();
    }

    private static PaymentRequestException Invalid(string message) => new(400, "invalid_payment_advice", message);
}

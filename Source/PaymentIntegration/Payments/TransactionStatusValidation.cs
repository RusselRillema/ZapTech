using System.Globalization;
using System.Text.Json;

namespace PaymentIntegration.Payments;

internal static class TransactionStatusValidation
{
    public static string Validate(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object)
            throw Invalid("Transaction Status must be a JSON object.");

        Required(payload, "transactionUUID");
        if (Required(payload, "transactionType") is not ("Reversal" or "Refund"))
            throw Invalid("transactionType must be Reversal or Refund.");
        var rrn = Required(payload, "rrn");
        if (rrn.Length > 12)
            throw Invalid("rrn must contain at most 12 characters.");
        if (!DateTime.TryParseExact(Required(payload, "transactionDateTime"), "yyyy-MM-dd'T'HH:mm:ss.fff",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw Invalid("transactionDateTime must use Acme's SAST timestamp format.");
        if (!payload.TryGetProperty("transactionAmount", out var amount)
            || amount.ValueKind != JsonValueKind.Number || !amount.TryGetInt64(out _))
            throw Invalid("transactionAmount must be an integer in the currency's lowest monetary unit.");
        if (payload.TryGetProperty("currencyCode", out var currency) && currency.ValueKind != JsonValueKind.Null
            && (currency.ValueKind != JsonValueKind.String || currency.GetString() is not { Length: 3 } code
                || code.Any(character => character is < 'A' or > 'Z')))
            throw Invalid("currencyCode must contain three uppercase letters.");
        Required(payload, "txResultCode");
        if (!payload.TryGetProperty("isApproved", out var approved)
            || approved.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw Invalid("isApproved must be a boolean.");
        return rrn;
    }

    private static string Required(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw Invalid($"{name} is required and must be a string.");

    private static PaymentRequestException Invalid(string message) => new(400, "invalid_transaction_status", message);
}

namespace PaymentIntegration.Payments;

public static class PaymentValidation
{
    public static void Validate(NewPaymentRequest request)
    {
        ValidateAppPaymentId(request.AppPaymentId);
        if (string.IsNullOrWhiteSpace(request.CustomerId))
            throw Invalid("CustomerId is required.");
        if (request.CustomerCardId == Guid.Empty)
            throw Invalid("CustomerCardId is required.");
        if (string.IsNullOrWhiteSpace(request.SubMerchantId) || request.SubMerchantId.Length > 15)
            throw Invalid("SubMerchantId is required and must contain at most 15 characters.");
        if (request.Amount <= 0)
            throw Invalid("Amount must be a positive integer in the currency's lowest monetary unit.");
        if (request.CurrencyCode is not { Length: 3 }
            || !request.CurrencyCode.All(character => character is >= 'A' and <= 'Z'))
            throw Invalid("CurrencyCode must contain three uppercase ISO currency-code letters.");
    }

    public static void ValidateAppPaymentId(string? appPaymentId)
    {
        if (string.IsNullOrWhiteSpace(appPaymentId))
            throw Invalid("AppPaymentId is required.");
    }

    private static PaymentRequestException Invalid(string message) => new(400, "invalid_payment", message);
}

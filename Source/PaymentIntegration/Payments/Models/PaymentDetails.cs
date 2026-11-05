namespace PaymentIntegration.Payments.Models;

public sealed record PaymentDetails(string AcmeCardToken, string Email, string TradingName,
    string City, string RegionCode, string CountryCode, string MerchantCategoryCode);

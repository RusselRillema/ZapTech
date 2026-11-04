namespace PaymentIntegration.Acme;

public sealed record AcmeSettings(Uri BaseUrl, string MerchantId, string ApiKey)
{
    public static AcmeSettings Read(IConfiguration configuration)
    {
        string Required(string name) => !string.IsNullOrWhiteSpace(configuration[name])
            ? configuration[name]!
            : throw new InvalidOperationException($"Set the {name} environment variable.");

        if (!Uri.TryCreate(Required("ACME_BASE_URL"), UriKind.Absolute, out var baseUrl)
            || (baseUrl.Scheme != Uri.UriSchemeHttp && baseUrl.Scheme != Uri.UriSchemeHttps)
            || baseUrl.UserInfo.Length != 0 || baseUrl.Query.Length != 0 || baseUrl.Fragment.Length != 0)
            throw new InvalidOperationException("ACME_BASE_URL must be an HTTP(S) URL without credentials, query or fragment.");
        var merchantId = Required("ACME_MERCHANT_ID");
        if (!Guid.TryParse(merchantId, out _))
            throw new InvalidOperationException("ACME_MERCHANT_ID must be a GUID.");
        return new(baseUrl, merchantId, Required("ACME_API_KEY"));
    }
}

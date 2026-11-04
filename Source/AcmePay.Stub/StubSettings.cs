namespace AcmePay.Stub;

public sealed record StubSettings(
    string ApiKey,
    Guid MerchantId,
    Uri AdviceUrl,
    string AdviceUsername,
    string AdvicePassword,
    Uri StatusWebhookUrl,
    string WebhookSecret,
    bool EnableControls)
{
    public static StubSettings Read(IConfiguration configuration)
    {
        string Required(string name) => !string.IsNullOrWhiteSpace(configuration[name])
            ? configuration[name]!
            : throw new InvalidOperationException($"Set the {name} environment variable.");

        Uri CallbackUrl(string name)
        {
            if (!Uri.TryCreate(Required(name), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || uri.UserInfo.Length != 0)
                throw new InvalidOperationException($"{name} must be an HTTP(S) URL without embedded credentials.");
            return uri;
        }

        var key = Required("ACME_API_KEY");
        if (!key.StartsWith("sk_test_", StringComparison.Ordinal))
            throw new InvalidOperationException("ACME_API_KEY must be a sandbox key starting with sk_test_.");
        if (!Guid.TryParse(Required("ACME_MERCHANT_ID"), out var merchantId))
            throw new InvalidOperationException("ACME_MERCHANT_ID must be a GUID.");
        var username = Required("ACME_ADVICE_USERNAME");
        if (username.Contains(':'))
            throw new InvalidOperationException("ACME_ADVICE_USERNAME cannot contain a colon.");
        var controls = configuration["ACME_ENABLE_CONTROLS"] ?? "false";
        if (!bool.TryParse(controls, out var enableControls))
            throw new InvalidOperationException("ACME_ENABLE_CONTROLS must be true or false.");

        return new(key, merchantId, CallbackUrl("ACME_ADVICE_URL"), username,
            Required("ACME_ADVICE_PASSWORD"), CallbackUrl("ACME_STATUS_WEBHOOK_URL"),
            Required("ACME_WEBHOOK_SECRET"), enableControls);
    }
}

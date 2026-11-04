using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace PaymentIntegration.Acme;

public sealed class AcmeAdviceSettings(Guid merchantId, string username, string password)
{
    private readonly byte[] credentialHash = SHA256.HashData(Encoding.UTF8.GetBytes($"{username}:{password}"));
    public Guid MerchantId { get; } = merchantId;

    public static AcmeAdviceSettings Read(IConfiguration configuration)
    {
        string Required(string name) => !string.IsNullOrWhiteSpace(configuration[name])
            ? configuration[name]!
            : throw new InvalidOperationException($"Set the {name} environment variable.");

        if (!Guid.TryParse(Required("ACME_MERCHANT_ID"), out var merchantId))
            throw new InvalidOperationException("ACME_MERCHANT_ID must be a GUID.");
        var username = Required("ACME_ADVICE_USERNAME");
        if (username.Contains(':'))
            throw new InvalidOperationException("ACME_ADVICE_USERNAME cannot contain a colon.");
        return new(merchantId, username, Required("ACME_ADVICE_PASSWORD"));
    }

    public bool Authenticates(string authorization)
    {
        if (!AuthenticationHeaderValue.TryParse(authorization, out var header)
            || !string.Equals(header.Scheme, "Basic", StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(header.Parameter))
            return false;

        try
        {
            var supplied = SHA256.HashData(Convert.FromBase64String(header.Parameter));
            return CryptographicOperations.FixedTimeEquals(credentialHash, supplied);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}

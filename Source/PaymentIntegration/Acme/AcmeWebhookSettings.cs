using System.Security.Cryptography;
using System.Text;

namespace PaymentIntegration.Acme;

public sealed class AcmeWebhookSettings(string secret)
{
    private readonly byte[] key = Encoding.UTF8.GetBytes(secret);

    public static AcmeWebhookSettings Read(IConfiguration configuration)
    {
        var secret = configuration["ACME_WEBHOOK_SECRET"];
        return !string.IsNullOrWhiteSpace(secret)
            ? new(secret)
            : throw new InvalidOperationException("Set the ACME_WEBHOOK_SECRET environment variable.");
    }

    public bool Authenticates(ReadOnlySpan<byte> body, string signature)
    {
        Span<byte> supplied = stackalloc byte[SHA256.HashSizeInBytes];
        if (!Convert.TryFromBase64String(signature, supplied, out var written)
            || written != supplied.Length)
            return false;

        Span<byte> expected = stackalloc byte[SHA256.HashSizeInBytes];
        HMACSHA256.HashData(key, body, expected);
        return CryptographicOperations.FixedTimeEquals(expected, supplied);
    }
}

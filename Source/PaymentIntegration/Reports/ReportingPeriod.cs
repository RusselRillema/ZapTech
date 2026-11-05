using System.Globalization;
using System.Text.Json;
using PaymentIntegration.Payments.Models;

namespace PaymentIntegration.Reports;

public sealed record ReportingPeriod(DateOnly Date, DateTimeOffset Start, DateTimeOffset End)
{
    public static readonly TimeZoneInfo TimeZone = TimeZoneInfo.FindSystemTimeZoneById("Africa/Johannesburg");

    public static ReportingPeriod ForDate(DateOnly date)
    {
        var start = TimeZoneInfo.ConvertTimeToUtc(date.ToDateTime(TimeOnly.MinValue), TimeZone);
        var end = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeZone);
        return new(date, new DateTimeOffset(start), new DateTimeOffset(end));
    }

    public bool Contains(DateTimeOffset timestamp) => timestamp >= Start && timestamp < End;

    public static DateTimeOffset EffectiveAt(PaymentEvent receipt)
    {
        if (receipt.Payload is not { ValueKind: JsonValueKind.Object } payload)
            return receipt.ReceivedAt;

        var body = payload;
        var field = receipt.Source == "TransactionStatus" ? "transactionDateTime" : "TransactionDateTime";
        if (receipt.Source is "PaymentResponse" or "TransactionQuery")
        {
            if (!payload.TryGetProperty("PaymentResponse", out body) || body.ValueKind != JsonValueKind.Object)
                return receipt.ReceivedAt;
        }

        if (!body.TryGetProperty(field, out var value) || value.ValueKind == JsonValueKind.Null)
            return receipt.ReceivedAt;
        if (value.ValueKind != JsonValueKind.String)
            throw new FormatException("An Acme transaction timestamp has an invalid type.");
        var text = value.GetString();
        if (string.IsNullOrWhiteSpace(text))
            return receipt.ReceivedAt;

        string[] localFormats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss"];
        if (DateTime.TryParseExact(text, localFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, TimeZone));

        string[] offsetFormats =
        [
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz", "yyyy-MM-dd'T'HH:mm:sszzz",
            "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'"
        ];
        if (DateTimeOffset.TryParseExact(text, offsetFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var instant))
            return instant;

        throw new FormatException("An Acme transaction timestamp is not a valid ISO transaction time.");
    }
}

namespace PaymentIntegration.Demo;

internal sealed record DemoSettings(Uri PaymentBaseUrl, Uri AcmeBaseUrl, string ApiKey,
    string CustomerId, Guid CustomerCardId, string SubMerchantId, long Amount, string CurrencyCode)
{
    public static DemoSettings Read()
    {
        static string Setting(string name, string fallback) => Environment.GetEnvironmentVariable(name) ?? fallback;
        static Uri Url(string name, string fallback)
        {
            if (!Uri.TryCreate(Setting(name, fallback), UriKind.Absolute, out var uri)
                || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length != 0
                || uri.Query.Length != 0 || uri.Fragment.Length != 0)
                throw new InvalidOperationException($"{name} must be an HTTP(S) URL without credentials, query or fragment.");
            return uri;
        }
        var apiKey = Setting("ACME_API_KEY", "");
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Set ACME_API_KEY to the key configured in the demo stub.");
        if (!Guid.TryParse(Setting("DEMO_CUSTOMER_CARD_ID", "8ee3f008-84f5-4152-80a5-21821abdd045"), out var card))
            throw new InvalidOperationException("DEMO_CUSTOMER_CARD_ID must be a stored-card GUID.");
        if (!long.TryParse(Setting("DEMO_AMOUNT", "1999"), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var amount) || amount <= 0)
            throw new InvalidOperationException("DEMO_AMOUNT must be a positive integer in the currency's lowest unit.");
        return new(Url("DEMO_PAYMENT_BASE_URL", "http://127.0.0.1:5080"),
            Url("DEMO_ACME_BASE_URL", "http://127.0.0.1:5081"), apiKey,
            Setting("DEMO_CUSTOMER_ID", "860e4537-28e4-4b83-a399-d4bd13f63a26"), card,
            Setting("DEMO_SUB_MERCHANT_ID", "ZAP9193145856"), amount, Setting("DEMO_CURRENCY_CODE", "ZAR"));
    }
}

internal sealed record DemoPaymentRequest(string AppPaymentId, string CustomerId, Guid CustomerCardId,
    string SubMerchantId, long Amount, string CurrencyCode);
internal sealed record ObservedPayment(Guid PaymentId, string AppPaymentId, string State, string CurrencyCode,
    long RequestedAmount, long? ApprovedAmount, long? RemainingAmount, bool IsFullyReconciled);
internal sealed record Scenario(string PaymentOutcome = "Approved", int ResponseDelaySeconds = 0, bool HoldCallbacks = true);
internal sealed record ObservedCallback(string Identifier, string Kind, bool IsHeld,
    int CompletedAttempts, int Acknowledgements, int? LastHttpStatusCode);
internal sealed record ObservedTransaction(int SubmissionCount, bool ResponseInterrupted, int ResponseDelaySeconds);
internal sealed record TriggeredAdjustment(string TransactionUUID, string Rrn);

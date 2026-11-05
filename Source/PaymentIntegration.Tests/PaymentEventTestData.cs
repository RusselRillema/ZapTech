namespace PaymentIntegration.Tests;

internal static class PaymentEventTestData
{
    public static readonly Guid PaymentId = Guid.Parse("2e0e1b85-bf91-45a9-b038-282e00036919");
    public const string Rrn = "ORDER1234567";

    public static Dictionary<string, object?> QueryFields(string state) => new()
    {
        ["TransactionID"] = PaymentId, ["OrderNumber"] = Rrn, ["TransactionStatus"] = state,
        ["ResponseDetail"] = new { Source = "Acquirer", Code = "00" }, ["AuthAmount"] = 1250L
    };

    public static Dictionary<string, object?> Fields(string type, long amount, string identifier = "event-1") => new()
    {
        ["transactionUUID"] = identifier, ["transactionType"] = type,
        ["transactionDateTime"] = "2026-03-04T08:02:11.417", ["rrn"] = Rrn,
        ["transactionAmount"] = amount, ["currencyCode"] = "ZAR", ["txResultCode"] = "00", ["isApproved"] = true
    };
}

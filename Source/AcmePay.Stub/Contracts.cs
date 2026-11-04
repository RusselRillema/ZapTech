using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AcmePay.Stub;

public static class Wire
{
    public static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static string Timestamp(DateTimeOffset time) =>
        time.ToOffset(TimeSpan.FromHours(2)).ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture);

    public static MessageHeader Header(TimeProvider clock, string channel = "App") =>
        new(Timestamp(clock.GetUtcNow()), Guid.NewGuid().ToString(), channel);

    public static IResult Result(PaymentResponse response, TimeProvider clock, string channel = "App", int status = 200) =>
        Results.Json(new PaymentReply(Header(clock, channel), response), Json, statusCode: status);

    public static IResult Error(int status, string code, string description, TimeProvider clock,
        string? transactionId = null, string? orderNumber = null, string channel = "App") =>
        Result(new PaymentResponse(transactionId, orderNumber, "Failure",
            new ResponseDetail("Application", code, description, "We could not process that payment.")),
            clock, channel, status);
}

public sealed record MessageHeader(string? MessageDateTime, string? MessageID, string? Channel);
public sealed record PaymentEnvelope(MessageHeader? MessageHeader, PaymentRequest? PaymentRequest);
public sealed record PaymentReply(MessageHeader MessageHeader, PaymentResponse PaymentResponse);

public sealed record PaymentRequest
{
    public string? MerchantID { get; init; }
    public string? TransactionID { get; init; }
    public string? OrderNumber { get; init; }
    public long? Amount { get; init; }
    public string? CurrencyCode { get; init; }
    public string? PaymentService { get; init; }
    public Card? Card { get; init; }
    public string? CardOnFileScenario { get; init; }
    public string? SubMerchantID { get; init; }
    public string? MerchantCategoryCode { get; init; }
    public string? CardAcceptorName { get; init; }
    public string? Email { get; init; }
}

public sealed record Card
{
    public string? Token { get; init; }

    // This endpoint accepts a stored token, never card details.
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? ExtraFields { get; init; }
}

public sealed record ResponseDetail(string Source, string Code, string Description, string ClientMessage);

public sealed record PaymentResponse(
    string? TransactionID,
    string? OrderNumber,
    string TransactionStatus,
    ResponseDetail ResponseDetail)
{
    public string? TransactionDateTime { get; init; }
    public string? ReconID { get; init; }
    public string? RetrievalReferenceNumber { get; init; }
    public string? AuthCode { get; init; }
    public long? AuthAmount { get; init; }
}

public sealed record PaymentAdvice(
    string RequestType,
    string MerchantID,
    string TransactionID,
    string TransactionDateTime,
    string OrderNumber,
    string ReconID,
    string RetrievalReferenceNumber,
    string SystemsTraceAuditNumber,
    long Amount,
    string CurrencyCode,
    string PaymentService,
    string TransactionStatus,
    string ResponseSource,
    string ResponseCode,
    string ResponseDescription,
    string AuthCode,
    string CommProtocol = "REST");

public sealed record StatusEvent(
    [property: JsonPropertyName("transactionUUID")] string TransactionUUID,
    [property: JsonPropertyName("transactionType")] string TransactionType,
    [property: JsonPropertyName("transactionDateTime")] string TransactionDateTime,
    [property: JsonPropertyName("rrn")] string Rrn,
    [property: JsonPropertyName("transactionAmount")] long TransactionAmount,
    [property: JsonPropertyName("currencyCode")] string CurrencyCode,
    [property: JsonPropertyName("txResultCode")] string TxResultCode,
    [property: JsonPropertyName("isApproved")] bool IsApproved);

public sealed record StatusRequest(long? Amount);

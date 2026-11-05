using System.Text.Json;

namespace PaymentIntegration.Acme.Contracts;

public sealed record AcmeHeader(string MessageDateTime, string MessageID, string Channel);
public sealed record AcmeCard(string Token);
public sealed record AcmePaymentRequest(string MerchantID, string TransactionID, string OrderNumber,
    long Amount, string CurrencyCode, string PaymentService, AcmeCard Card,
    string CardOnFileScenario, string SubMerchantID, string MerchantCategoryCode,
    string CardAcceptorName, string Email);
public sealed record AcmeEnvelope(AcmeHeader MessageHeader, AcmePaymentRequest PaymentRequest);
public sealed record AcmeReply(
    short? HttpStatusCode,
    JsonElement? Payload,
    string? Failure = null,
    TimeSpan? RetryDelay = null,
    bool RequestAttempted = true);

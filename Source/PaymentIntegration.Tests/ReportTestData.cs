using System.Text.Json;
using PaymentIntegration.Payments;
using PaymentIntegration.Reports;

namespace PaymentIntegration.Tests;

internal static class ReportTestData
{
    public static Payment Payment() => new(Guid.NewGuid(), "report-payment", "customer", Guid.NewGuid(),
        "merchant-a", "ORDER1234567", "ZAR", 100000, "Unresolved",
        DateTimeOffset.Parse("2026-11-03T21:59:00Z"));

    public static PaymentEvent Outcome(Payment payment, string? transactionTime = "2026-11-04T00:01:00.000",
        string receivedAt = "2026-11-03T22:01:00Z", string code = "00", long? amount = 100000) =>
        new(Guid.NewGuid(), payment.PaymentId, "PaymentResponse", JsonSerializer.SerializeToElement(new
        {
            PaymentResponse = new
            {
                TransactionID = payment.PaymentId.ToString(), payment.OrderNumber,
                TransactionDateTime = transactionTime,
                TransactionStatus = "Success",
                ResponseDetail = new { Source = "Acquirer", Code = code },
                AuthAmount = amount
            }
        }), 200, DateTimeOffset.Parse(receivedAt));

    public static PaymentEvent Adjustment(Payment payment, string type = "Refund", long amount = 30000,
        string timestamp = "2026-11-05T10:00:00.000", string receivedAt = "2026-11-05T08:00:00Z",
        string identifier = "adjustment-1") =>
        new(Guid.NewGuid(), payment.PaymentId, "TransactionStatus", JsonSerializer.SerializeToElement(new
        {
            transactionUUID = identifier, transactionType = type, transactionDateTime = timestamp,
            rrn = payment.OrderNumber, transactionAmount = amount, currencyCode = payment.CurrencyCode,
            txResultCode = "00", isApproved = true
        }), null, DateTimeOffset.Parse(receivedAt));

    public static PaymentHistoryReconstructor Reconstructor()
    {
        var status = new TransactionStatusProcessor();
        var processor = new PaymentEventProcessor(new PaymentResponseProcessor(status), status);
        return new(processor, Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentHistoryReconstructor>.Instance);
    }
}

namespace PaymentIntegration.Payments.Models;

public sealed class PaymentRequestException(int statusCode, string code, string message)
    : Exception(message)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
}

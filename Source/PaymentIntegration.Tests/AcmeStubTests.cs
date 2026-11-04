using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class AcmeStubTests
{
    [Fact]
    public async Task Payment_approves_and_sends_basic_authenticated_advice_without_the_card_token()
    {
        await using var host = await StubHost.StartAsync();
        var request = StubHost.Payment();
        using var response = await host.SubmitAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        var payment = body.RootElement.GetProperty("PaymentResponse");
        Assert.Equal("Success", payment.GetProperty("TransactionStatus").GetString());
        Assert.Equal("00", payment.GetProperty("ResponseDetail").GetProperty("Code").GetString());
        Assert.Equal(1999, payment.GetProperty("AuthAmount").GetInt64());
        Assert.Equal("2026-03-01T13:56:36.533", payment.GetProperty("TransactionDateTime").GetString());
        Assert.NotEqual(request["MessageHeader"]!["MessageID"]!.GetValue<string>(),
            body.RootElement.GetProperty("MessageHeader").GetProperty("MessageID").GetString());
        Assert.DoesNotContain("stored-card-token", body.RootElement.GetRawText());

        var callback = await host.NextCallbackAsync();
        Assert.Equal("/advice", callback.Path);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{StubHost.Username}:{StubHost.Password}")),
            callback.Authorization);
        Assert.Empty(callback.Signature);
        using var advice = JsonDocument.Parse(callback.Body);
        Assert.Equal("ZAR", advice.RootElement.GetProperty("CurrencyCode").GetString());
        Assert.Equal("attempt-1", advice.RootElement.GetProperty("TransactionID").GetString());
        Assert.Equal("00", advice.RootElement.GetProperty("ResponseCode").GetString());
        Assert.Equal("REST", advice.RootElement.GetProperty("CommProtocol").GetString());
        Assert.DoesNotContain("stored-card-token", advice.RootElement.GetRawText());
    }

    [Theory]
    [InlineData("/api/payment", "POST", null)]
    [InlineData("/api/payment", "POST", "Bearer wrong")]
    [InlineData("/apirttq/query/Merchant/8B67BF88-BB8D-4EB5-ADFC-0F5C97C7ED67?TransactionID=attempt-1", "GET", null)]
    [InlineData("/apirttq/query/Merchant/8B67BF88-BB8D-4EB5-ADFC-0F5C97C7ED67?TransactionID=attempt-1", "GET", "Basic wrong")]
    [InlineData("/__stub/transactions/attempt-1/refund", "POST", "Bearer sk_test_wrong")]
    public async Task Missing_or_invalid_bearer_key_is_rejected_before_processing(string path, string method, string? authorization)
    {
        await using var host = await StubHost.StartAsync();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        host.Client.DefaultRequestHeaders.Authorization = null;
        if (authorization is not null)
            request.Headers.Authorization = AuthenticationHeaderValue.Parse(authorization);
        using var response = await host.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("E02", body.RootElement.GetProperty("PaymentResponse").GetProperty("ResponseDetail").GetProperty("Code").GetString());
        Assert.Empty(host.Deliveries);
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("token")]
    [InlineData("card-number")]
    [InlineData("merchant")]
    [InlineData("descriptor")]
    [InlineData("order")]
    public async Task Invalid_payment_is_rejected_without_creating_a_transaction(string invalidField)
    {
        await using var host = await StubHost.StartAsync();
        var request = StubHost.Payment();
        var payment = request["PaymentRequest"]!;
        switch (invalidField)
        {
            case "amount": payment["Amount"] = null; break;
            case "token": payment["Card"] = null; break;
            case "card-number": payment["Card"]!["CardNumber"] = "5413330000001429"; break;
            case "merchant": payment["MerchantID"] = Guid.Empty.ToString(); break;
            case "descriptor": payment["CardAcceptorName"] = "ZAP*SHORT"; break;
            case "order": payment["OrderNumber"] = "invalid-order"; break;
        }
        using var response = await host.SubmitAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("E01", body.RootElement.GetProperty("PaymentResponse").GetProperty("ResponseDetail").GetProperty("Code").GetString());
        using var query = await host.QueryAsync();
        Assert.Equal(HttpStatusCode.NotFound, query.StatusCode);
        Assert.Empty(host.Deliveries);
    }

    [Fact]
    public async Task Malformed_json_returns_the_Acme_error_envelope()
    {
        await using var host = await StubHost.StartAsync();
        using var response = await host.Client.PostAsync("/api/payment", new StringContent("{", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("E01", body.RootElement.GetProperty("PaymentResponse").GetProperty("ResponseDetail").GetProperty("Code").GetString());
    }

    [Fact]
    public async Task Query_by_either_identifier_returns_the_stored_transaction()
    {
        await using var host = await StubHost.StartAsync();
        using var submitted = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var original = JsonDocument.Parse(await submitted.Content.ReadAsByteArrayAsync());
        var expected = original.RootElement.GetProperty("PaymentResponse").GetRawText();
        foreach (var queryString in new[] { "TransactionID=attempt-1", "OrderNumber=MR43851259" })
        {
            using var response = await host.QueryAsync(queryString);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var result = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
            Assert.Equal(expected, result.RootElement.GetProperty("PaymentResponse").GetRawText());
        }
        using var absent = await host.QueryAsync("TransactionID=unknown");
        Assert.Equal(HttpStatusCode.NotFound, absent.StatusCode);
        using var otherMerchant = await host.Client.GetAsync($"/apirttq/query/Merchant/{Guid.Empty}?TransactionID=attempt-1");
        Assert.Equal(HttpStatusCode.NotFound, otherMerchant.StatusCode);
    }

    [Fact]
    public async Task Concurrent_repeated_submissions_create_one_payment_and_one_advice()
    {
        await using var host = await StubHost.StartAsync();
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => host.SubmitAsync()));
        try
        {
            var reconIds = new List<string>();
            var messageIds = new HashSet<string>();
            foreach (var response in responses)
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
                reconIds.Add(body.RootElement.GetProperty("PaymentResponse").GetProperty("ReconID").GetString()!);
                messageIds.Add(body.RootElement.GetProperty("MessageHeader").GetProperty("MessageID").GetString()!);
            }
            Assert.Single(reconIds.Distinct());
            Assert.Equal(8, messageIds.Count);
            await host.StopStubAsync();
            Assert.Single(host.Deliveries);
        }
        finally
        {
            foreach (var response in responses)
                response.Dispose();
        }
    }

    [Fact]
    public async Task Colliding_RRN_returns_the_existing_transactions_identifier()
    {
        await using var host = await StubHost.StartAsync();
        using var original = await host.SubmitAsync(StubHost.Payment("first", "ABCDEFGHIJKL1"));
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        using var collision = await host.SubmitAsync(StubHost.Payment("second", "ABCDEFGHIJKL2"));
        Assert.Equal(HttpStatusCode.Conflict, collision.StatusCode);
        using var body = JsonDocument.Parse(await collision.Content.ReadAsByteArrayAsync());
        var payment = body.RootElement.GetProperty("PaymentResponse");
        Assert.Equal("first", payment.GetProperty("TransactionID").GetString());
        Assert.Equal("E09", payment.GetProperty("ResponseDetail").GetProperty("Code").GetString());
        await host.StopStubAsync();
        Assert.Single(host.Deliveries);
    }

    [Fact]
    public async Task Transaction_replay_expires_after_24_hours_while_RRNs_remain_reserved()
    {
        await using var host = await StubHost.StartAsync();
        using var original = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, original.StatusCode);
        host.Clock.Now += TimeSpan.FromHours(24);
        using var repeated = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
    }

    [Theory]
    [InlineData("reversal", "Reversal", "Reversed")]
    [InlineData("refund", "Refund", "Refunded")]
    public async Task Status_trigger_sends_a_signed_event_and_updates_the_query(string route, string type, string status)
    {
        await using var host = await StubHost.StartAsync();
        using var submitted = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        await host.NextCallbackAsync();
        using var triggered = await host.Client.PostAsync($"/__stub/transactions/attempt-1/{route}",
            new StringContent("{\"Amount\":1999}", Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.Accepted, triggered.StatusCode);
        var callback = await host.NextCallbackAsync();
        Assert.Equal("/status", callback.Path);
        Assert.Empty(callback.Authorization);
        // Verify as a receiver against the actual delivered bytes, including whitespace and casing.
        using var verifier = new HMACSHA256(Encoding.UTF8.GetBytes(StubHost.Secret));
        Assert.True(CryptographicOperations.FixedTimeEquals(verifier.ComputeHash(callback.Body), Convert.FromBase64String(callback.Signature)));
        using var body = JsonDocument.Parse(callback.Body);
        var payload = body.RootElement;
        Assert.Equal(type, payload.GetProperty("transactionType").GetString());
        Assert.Equal("MR43851259", payload.GetProperty("rrn").GetString());
        Assert.Equal(1999, payload.GetProperty("transactionAmount").GetInt64());
        Assert.Equal("ZAR", payload.GetProperty("currencyCode").GetString());
        Assert.Equal("00", payload.GetProperty("txResultCode").GetString());
        Assert.True(payload.GetProperty("isApproved").GetBoolean());
        Assert.NotEqual("attempt-1", payload.GetProperty("transactionUUID").GetString());
        using var query = await host.QueryAsync();
        using var result = JsonDocument.Parse(await query.Content.ReadAsByteArrayAsync());
        Assert.Equal(status, result.RootElement.GetProperty("PaymentResponse").GetProperty("TransactionStatus").GetString());
        using var repeated = await host.Client.PostAsJsonAsync($"/__stub/transactions/attempt-1/{route}", new { Amount = 1999 });
        Assert.Equal(HttpStatusCode.Conflict, repeated.StatusCode);
    }

    [Fact]
    public async Task Partial_refund_is_explicitly_unimplemented_and_leaves_the_payment_approved()
    {
        await using var host = await StubHost.StartAsync();
        using var submitted = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var response = await host.Client.PostAsJsonAsync("/__stub/transactions/attempt-1/refund", new { Amount = 1000 });
        Assert.Equal(HttpStatusCode.NotImplemented, response.StatusCode);
        using var query = await host.QueryAsync();
        using var body = JsonDocument.Parse(await query.Content.ReadAsByteArrayAsync());
        Assert.Equal("Success", body.RootElement.GetProperty("PaymentResponse").GetProperty("TransactionStatus").GetString());
        await host.StopStubAsync();
        Assert.Single(host.Deliveries);
    }

    [Fact]
    public async Task Local_triggers_are_absent_unless_enabled()
    {
        await using var host = await StubHost.StartAsync(enableControls: false);
        using var response = await host.Client.PostAsJsonAsync("/__stub/transactions/attempt-1/refund", new { Amount = 1999 });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData(false, 200)]
    [InlineData(true, 200)]
    [InlineData(true, 503)]
    public async Task Callbacks_retry_three_times_with_identical_bodies_and_authentication(bool webhook, int lastStatus)
    {
        await using var host = await StubHost.StartAsync();
        if (webhook)
        {
            using var payment = await host.SubmitAsync();
            Assert.Equal(HttpStatusCode.OK, payment.StatusCode);
            await host.NextCallbackAsync();
        }
        foreach (var status in new[] { 503, 204, 500, lastStatus })
            host.CallbackResponses.Enqueue(status);
        using var triggered = webhook
            ? await host.Client.PostAsJsonAsync("/__stub/transactions/attempt-1/refund", new { Amount = 1999 })
            : await host.SubmitAsync();
        Assert.Equal(webhook ? HttpStatusCode.Accepted : HttpStatusCode.OK, triggered.StatusCode);
        var first = await host.NextCallbackAsync();
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var retried = await host.NextCallbackAsync();
            Assert.Equal(first.Body, retried.Body);
            Assert.Equal(first.Signature, retried.Signature);
            Assert.Equal(first.Authorization, retried.Authorization);
        }
        await host.StopStubAsync();
        Assert.Equal(webhook ? 5 : 4, host.Deliveries.Count);
    }
}

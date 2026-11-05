using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace AcmePay.Stub.Tests;

public sealed class DemoScenarioTests
{
    [Fact]
    public async Task Decline_is_consistent_across_response_advice_and_query_and_survives_scenario_changes()
    {
        await using var host = await StubHost.StartAsync();
        using var configured = await host.Client.PostAsJsonAsync("/__stub/scenario", new { paymentOutcome = "Declined" });
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);

        using var submitted = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var body = JsonDocument.Parse(await submitted.Content.ReadAsByteArrayAsync());
        var declined = body.RootElement.GetProperty("PaymentResponse");
        Assert.Equal("Success", declined.GetProperty("TransactionStatus").GetString());
        Assert.Equal("Acquirer", declined.GetProperty("ResponseDetail").GetProperty("Source").GetString());
        Assert.Equal("05", declined.GetProperty("ResponseDetail").GetProperty("Code").GetString());
        Assert.False(declined.TryGetProperty("AuthAmount", out _));
        Assert.False(declined.TryGetProperty("AuthCode", out _));
        var callback = await host.NextCallbackAsync();
        using var advice = JsonDocument.Parse(callback.Body);
        Assert.Equal("05", advice.RootElement.GetProperty("ResponseCode").GetString());
        Assert.Equal("attempt-1", advice.RootElement.GetProperty("TransactionID").GetString());

        using var configuredAgain = await host.Client.PostAsJsonAsync("/__stub/scenario", new { paymentOutcome = "Approved" });
        Assert.Equal(HttpStatusCode.NoContent, configuredAgain.StatusCode);
        using var replayed = await host.SubmitAsync();
        using var replay = JsonDocument.Parse(await replayed.Content.ReadAsByteArrayAsync());
        Assert.Equal(declined.GetRawText(), replay.RootElement.GetProperty("PaymentResponse").GetRawText());
        using var observed = await host.Client.GetAsync("/__stub/transactions/attempt-1");
        using var observation = JsonDocument.Parse(await observed.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, observation.RootElement.GetProperty("submissionCount").GetInt32());
        using var queried = await host.QueryAsync();
        using var query = JsonDocument.Parse(await queried.Content.ReadAsByteArrayAsync());
        Assert.Equal(declined.GetRawText(), query.RootElement.GetProperty("PaymentResponse").GetRawText());

        foreach (var type in new[] { "reversal", "refund" })
        {
            using var rejected = await host.Client.PostAsJsonAsync($"/__stub/transactions/attempt-1/{type}", new { Amount = 1999 });
            Assert.Equal(HttpStatusCode.Conflict, rejected.StatusCode);
        }

        using var newPayment = await host.SubmitAsync(StubHost.Payment("attempt-2", "MR43851260"));
        Assert.Equal(HttpStatusCode.OK, newPayment.StatusCode);
        using var approvedBody = JsonDocument.Parse(await newPayment.Content.ReadAsByteArrayAsync());
        Assert.Equal(1999, approvedBody.RootElement.GetProperty("PaymentResponse").GetProperty("AuthAmount").GetInt64());
        var newCallback = await host.NextCallbackAsync();
        using var approvedAdvice = JsonDocument.Parse(newCallback.Body);
        Assert.Equal("00", approvedAdvice.RootElement.GetProperty("ResponseCode").GetString());
        await host.StopStubAsync();
        Assert.Equal(2, host.Deliveries.Count);
    }

    [Theory]
    [InlineData("reversal", "Reversal", "Reversed")]
    [InlineData("refund", "Refund", "Refunded")]
    public async Task Held_callbacks_can_be_sent_out_of_order_and_replayed_with_original_identity_and_authentication(
        string route, string type, string state)
    {
        await using var host = await StubHost.StartAsync();
        using var configured = await host.Client.PostAsJsonAsync("/__stub/scenario", new { holdCallbacks = true });
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
        using var submitted = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        // An accepted transaction retains its callback settings when the default changes.
        using var reset = await host.Client.PostAsJsonAsync("/__stub/scenario", new { paymentOutcome = "Declined" });
        Assert.Equal(HttpStatusCode.NoContent, reset.StatusCode);
        using var triggered = await host.Client.PostAsJsonAsync($"/__stub/transactions/attempt-1/{route}", new { Amount = 1999 });
        Assert.Equal(HttpStatusCode.Accepted, triggered.StatusCode);
        using var trigger = JsonDocument.Parse(await triggered.Content.ReadAsByteArrayAsync());
        var identifier = trigger.RootElement.GetProperty("transactionUUID").GetString()!;
        using var listed = await host.Client.GetAsync("/__stub/transactions/attempt-1/callbacks");
        using var callbacks = JsonDocument.Parse(await listed.Content.ReadAsByteArrayAsync());
        Assert.Equal(2, callbacks.RootElement.GetArrayLength());
        Assert.All(callbacks.RootElement.EnumerateArray(), item => Assert.True(item.GetProperty("isHeld").GetBoolean()));
        Assert.Empty(host.Deliveries);

        using var unknown = await host.Client.PostAsync("/__stub/transactions/attempt-1/callbacks/unknown/send", null);
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        using var wrongPayment = await host.Client.PostAsync($"/__stub/transactions/other/callbacks/{identifier}/send", null);
        Assert.Equal(HttpStatusCode.NotFound, wrongPayment.StatusCode);

        var status = await SendAsync(host, identifier);
        Assert.Equal("/status", status.Path);
        using var statusBody = JsonDocument.Parse(status.Body);
        Assert.Equal(type, statusBody.RootElement.GetProperty("transactionType").GetString());
        Assert.Equal(identifier, statusBody.RootElement.GetProperty("transactionUUID").GetString());
        Assert.Equal(Convert.ToBase64String(HMACSHA256.HashData(Encoding.UTF8.GetBytes(StubHost.Secret), status.Body)), status.Signature);
        var advice = await SendAsync(host, "attempt-1");
        Assert.Equal("/advice", advice.Path);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{StubHost.Username}:{StubHost.Password}")), advice.Authorization);

        var repeatedStatus = await SendAsync(host, identifier);
        Assert.Equal(status.Body, repeatedStatus.Body);
        Assert.Equal(status.Signature, repeatedStatus.Signature);
        var repeatedAdvice = await SendAsync(host, "attempt-1");
        Assert.Equal(advice.Body, repeatedAdvice.Body);
        Assert.Equal(advice.Authorization, repeatedAdvice.Authorization);
        using var queried = await host.QueryAsync();
        using var query = JsonDocument.Parse(await queried.Content.ReadAsByteArrayAsync());
        Assert.Equal(state, query.RootElement.GetProperty("PaymentResponse").GetProperty("TransactionStatus").GetString());
        await host.StopStubAsync();
        Assert.Equal(4, host.Deliveries.Count);
    }

    [Fact]
    public async Task A_reversal_can_be_delivered_while_the_original_approval_reply_is_delayed()
    {
        await using var host = await StubHost.StartAsync();
        using var configured = await host.Client.PostAsJsonAsync("/__stub/scenario", new { responseDelaySeconds = 40, holdCallbacks = true });
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var submission = host.Client.PostAsJsonAsync("/api/payment", StubHost.Payment(), deadline.Token);
        await host.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(40));
        Assert.False(submission.IsCompleted);
        using var triggered = await host.Client.PostAsJsonAsync("/__stub/transactions/attempt-1/reversal", new { Amount = 1999 });
        Assert.Equal(HttpStatusCode.Accepted, triggered.StatusCode);
        using var trigger = JsonDocument.Parse(await triggered.Content.ReadAsByteArrayAsync());
        var identifier = trigger.RootElement.GetProperty("transactionUUID").GetString()!;
        var reversal = await SendAsync(host, identifier);
        Assert.Equal("/status", reversal.Path);
        Assert.False(submission.IsCompleted);

        host.Clock.Advance(TimeSpan.FromSeconds(40));
        using var response = await submission;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var approval = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal("Success", approval.RootElement.GetProperty("PaymentResponse").GetProperty("TransactionStatus").GetString());
        Assert.Equal(1999, approval.RootElement.GetProperty("PaymentResponse").GetProperty("AuthAmount").GetInt64());
        using var queried = await host.QueryAsync();
        using var current = JsonDocument.Parse(await queried.Content.ReadAsByteArrayAsync());
        Assert.Equal("Reversed", current.RootElement.GetProperty("PaymentResponse").GetProperty("TransactionStatus").GetString());
    }

    [Fact]
    public async Task Losing_a_delayed_reply_preserves_the_outcome_and_does_not_block_other_payments_or_RTTQ()
    {
        await using var host = await StubHost.StartAsync();
        using var configured = await host.Client.PostAsJsonAsync("/__stub/scenario", new { responseDelaySeconds = 40, holdCallbacks = true });
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
        using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var submission = host.Client.PostAsJsonAsync("/api/payment", StubHost.Payment(), caller.Token);
        await host.Clock.WaitForTimerAsync(TimeSpan.FromSeconds(40));
        using var queried = await host.QueryAsync();
        Assert.Equal(HttpStatusCode.OK, queried.StatusCode);
        using var query = JsonDocument.Parse(await queried.Content.ReadAsByteArrayAsync());
        Assert.Equal(1999, query.RootElement.GetProperty("PaymentResponse").GetProperty("AuthAmount").GetInt64());
        using var configuredAgain = await host.Client.PostAsJsonAsync("/__stub/scenario", new { paymentOutcome = "Declined", holdCallbacks = true });
        Assert.Equal(HttpStatusCode.NoContent, configuredAgain.StatusCode);
        using var other = await host.SubmitAsync(StubHost.Payment("attempt-2", "MR43851260"));
        Assert.Equal(HttpStatusCode.OK, other.StatusCode);
        using var otherBody = JsonDocument.Parse(await other.Content.ReadAsByteArrayAsync());
        Assert.Equal("05", otherBody.RootElement.GetProperty("PaymentResponse").GetProperty("ResponseDetail").GetProperty("Code").GetString());
        Assert.False(submission.IsCompleted);

        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { using var response = await submission; });
        using var observationDeadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var observed = await host.Client.GetAsync("/__stub/transactions/attempt-1", observationDeadline.Token);
            using var observation = JsonDocument.Parse(await observed.Content.ReadAsByteArrayAsync(observationDeadline.Token));
            if (observation.RootElement.GetProperty("responseInterrupted").GetBoolean())
                break;
            await Task.Delay(20, observationDeadline.Token);
        }
        using var repeated = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, repeated.StatusCode);
        using var replay = JsonDocument.Parse(await repeated.Content.ReadAsByteArrayAsync());
        Assert.Equal(query.RootElement.GetProperty("PaymentResponse").GetRawText(), replay.RootElement.GetProperty("PaymentResponse").GetRawText());
        await host.StopStubAsync();
        Assert.Empty(host.Deliveries);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"paymentOutcome\":\"Unknown\"}")]
    [InlineData("{\"paymentOutcome\":null}")]
    [InlineData("{\"responseDelaySeconds\":-1}")]
    [InlineData("{\"responseDelaySeconds\":61}")]
    [InlineData("{\"holdCallbacks\":\"yes\"}")]
    public async Task Invalid_scenario_updates_leave_the_previous_behaviour_in_effect(string invalidBody)
    {
        await using var host = await StubHost.StartAsync();
        using var configured = await host.Client.PostAsJsonAsync("/__stub/scenario", new { paymentOutcome = "Declined", holdCallbacks = true });
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
        using var rejected = await host.Client.PostAsync("/__stub/scenario", new StringContent(invalidBody, Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        using var submitted = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        using var body = JsonDocument.Parse(await submitted.Content.ReadAsByteArrayAsync());
        Assert.Equal("05", body.RootElement.GetProperty("PaymentResponse").GetProperty("ResponseDetail").GetProperty("Code").GetString());
        await host.StopStubAsync();
        Assert.Empty(host.Deliveries);
    }

    private static async Task<CapturedCallback> SendAsync(StubHost host, string identifier)
    {
        using var beforeResponse = await host.Client.GetAsync("/__stub/transactions/attempt-1/callbacks");
        using var before = JsonDocument.Parse(await beforeResponse.Content.ReadAsByteArrayAsync());
        var acknowledgements = before.RootElement.EnumerateArray()
            .Single(item => item.GetProperty("identifier").GetString() == identifier).GetProperty("acknowledgements").GetInt32();
        using var sent = await host.Client.PostAsync($"/__stub/transactions/attempt-1/callbacks/{identifier}/send", null);
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        var delivered = await host.NextCallbackAsync();
        var progress = await WaitForAcknowledgementAsync(host, identifier, acknowledgements + 1);
        Assert.Equal(200, progress.GetProperty("lastHttpStatusCode").GetInt32());
        return delivered;
    }

    [Fact]
    public async Task Callback_statistics_distinguish_failed_deliveries_from_a_successful_acknowledgement()
    {
        await using var host = await StubHost.StartAsync();
        using var configured = await host.Client.PostAsJsonAsync("/__stub/scenario", new { holdCallbacks = true });
        Assert.Equal(HttpStatusCode.NoContent, configured.StatusCode);
        using var submitted = await host.SubmitAsync();
        Assert.Equal(HttpStatusCode.OK, submitted.StatusCode);
        foreach (var status in new[] { 503, 204, 500, 200 })
            host.CallbackResponses.Enqueue(status);
        using var sent = await host.Client.PostAsync("/__stub/transactions/attempt-1/callbacks/attempt-1/send", null);
        Assert.Equal(HttpStatusCode.Accepted, sent.StatusCode);
        for (var attempt = 0; attempt < 4; attempt++)
            await host.NextCallbackAsync();
        var progress = await WaitForAcknowledgementAsync(host, "attempt-1", 1);
        Assert.Equal(4, progress.GetProperty("completedAttempts").GetInt32());
        Assert.Equal(1, progress.GetProperty("acknowledgements").GetInt32());
        Assert.Equal(200, progress.GetProperty("lastHttpStatusCode").GetInt32());
    }

    private static async Task<JsonElement> WaitForAcknowledgementAsync(StubHost host, string identifier, int minimum)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            using var response = await host.Client.GetAsync("/__stub/transactions/attempt-1/callbacks", deadline.Token);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            using var body = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(deadline.Token));
            var callback = body.RootElement.EnumerateArray().Single(item => item.GetProperty("identifier").GetString() == identifier);
            if (callback.GetProperty("acknowledgements").GetInt32() >= minimum)
                return callback.Clone();
            await Task.Delay(20, deadline.Token);
        }
    }
}

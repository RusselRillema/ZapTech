namespace PaymentIntegration.Demo;

internal static class DemoConsole
{
    public static string Describe(string scenario) => scenario switch
    {
        "approval" => "Approve a payment, then deliver Payment Advice.",
        "decline" => "Decline a payment, then deliver Payment Advice.",
        "duplicate-submission" => "Submit the same payment twice; check Acme received it once.",
        "duplicate-notification" => "Replay Payment Advice; check the result stays unchanged.",
        "out-of-order" => "Deliver Advice while the payment response waits for 15 seconds.",
        "timeout" => "Delay Acme for 40 seconds; observe timeout and transaction-query recovery.",
        "reversal" => "Approve a payment, then reverse it in full.",
        "refund" => "Approve a payment, then refund it in full.",
        "reversal-before-approval" => "Deliver a full reversal before the delayed approval response.",
        "refund-before-approval" => "Deliver a full refund before the delayed approval response.",
        "refund-then-reversal" => "Refund a payment, then deliver a full reversal.",
        "reversal-then-refund" => "Reverse a payment, then deliver a full refund.",
        _ => throw new ArgumentException("Unsupported scenario.", nameof(scenario))
    };

    public static async Task<int> RunAsync(TextReader input, TextWriter output,
        Func<string, CancellationToken, Task<int>> runScenario, CancellationToken cancellationToken)
    {
        var scenarios = DemoRunner.Scenarios.Concat(DemoRunner.AdjustmentScenarios).ToArray();
        var exitCode = 0;
        await output.WriteLineAsync("PaymentIntegration interactive demo");
        await output.WriteLineAsync("Use a dedicated running stack with ACME_ENABLE_CONTROLS=true. Runs retain payments for audit.");
        await output.WriteLineAsync("Choose a number and press Enter. Each scenario runs automatically, then returns to this menu.");
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await output.WriteLineAsync();
            for (var index = 0; index < scenarios.Length; index++)
                await output.WriteLineAsync($"{index + 1,2}. {scenarios[index]} - {Describe(scenarios[index])}");
            await output.WriteLineAsync(" 0. Exit (Ctrl+C cancels the demo)");
            await output.WriteAsync("Select scenario: ");
            // Console.In can block synchronously even through ReadLineAsync.
            // Read on a worker so Ctrl+C can cancel the wait while the menu is idle.
            var choice = (await Task.Run(input.ReadLine, cancellationToken).WaitAsync(cancellationToken))?.Trim();
            if (choice is null or "0" || string.Equals(choice, "q", StringComparison.OrdinalIgnoreCase))
                return exitCode;
            if (!int.TryParse(choice, out var number) || number < 1 || number > scenarios.Length)
            {
                await output.WriteLineAsync($"Enter a number from 1 to {scenarios.Length}, or 0 to exit.");
                continue;
            }

            var result = await runScenario(scenarios[number - 1], cancellationToken);
            if (result != 0)
                exitCode = result;
        }
    }
}

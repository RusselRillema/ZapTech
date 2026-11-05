using System.Net.Http.Headers;
using PaymentIntegration.Demo;

if (args is ["--list"])
{
    foreach (var scenario in DemoRunner.Scenarios)
        Console.WriteLine(scenario);
    return 0;
}
if (args is ["--list-adjustments"])
{
    foreach (var scenario in DemoRunner.AdjustmentScenarios)
        Console.WriteLine(scenario);
    return 0;
}
if (args is ["--help"] or ["-h"])
{
    Console.WriteLine("Usage: PaymentIntegration.Demo [<scenario> | --interactive | --list | --list-adjustments]");
    Console.WriteLine("Run without arguments (or with --interactive) for the numbered scenario menu.");
    Console.WriteLine("Scenarios: " + string.Join(", ", DemoRunner.Scenarios));
    Console.WriteLine("Use a dedicated Acme stub with ACME_ENABLE_CONTROLS=true; run scenarios sequentially.");
    Console.WriteLine("Set ACME_API_KEY; optionally DEMO_PAYMENT_BASE_URL, DEMO_ACME_BASE_URL and the DEMO_* catalogue settings described in README.md.");
    Console.WriteLine("Opt-in step 6 scenarios: " + string.Join(", ", DemoRunner.AdjustmentScenarios));
    Console.WriteLine("Adjustment demonstrations require the published Task 1 Step 6 implementation and its deployment SQL.");
    return 0;
}
var interactive = args.Length == 0 || args is ["--interactive"];
if (!interactive && (args.Length != 1 || !DemoRunner.Scenarios.Concat(DemoRunner.AdjustmentScenarios).Contains(args[0], StringComparer.Ordinal)))
{
    Console.Error.WriteLine("Choose one supported scenario, or use --interactive, --list or --help.");
    return 2;
}

using var cancellation = new CancellationTokenSource();
ConsoleCancelEventHandler cancel = (_, e) => { e.Cancel = true; cancellation.Cancel(); };
Console.CancelKeyPress += cancel;
try
{
    return interactive
        ? await DemoConsole.RunAsync(Console.In, Console.Out, RunScenarioAsync, cancellation.Token)
        : await RunScenarioAsync(args[0], cancellation.Token);
}
catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
{
    Console.Error.WriteLine("Demo cancelled.");
    return 130;
}
finally
{
    Console.CancelKeyPress -= cancel;
}

static async Task<int> RunScenarioAsync(string name, CancellationToken cancellationToken)
{
    // The deadline applies to each run, never to time spent choosing from the menu.
    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
    deadline.CancelAfter(TimeSpan.FromSeconds(90));
    try
    {
        if (File.Exists(".env"))
            DotNetEnv.Env.NoClobber().Load(".env");
        var settings = DemoSettings.Read();
        using var payments = new HttpClient { BaseAddress = settings.PaymentBaseUrl, Timeout = TimeSpan.FromSeconds(80) };
        using var acme = new HttpClient { BaseAddress = settings.AcmeBaseUrl, Timeout = TimeSpan.FromSeconds(10) };
        acme.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        await new DemoRunner(payments, acme, settings, Console.Out).RunAsync(name, deadline.Token);
        Console.WriteLine($"PASS {name}");
        return 0;
    }
    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
    {
        throw;
    }
    catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException
        or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
    {
        Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
        return 1;
    }
}

using PaymentIntegration.Demo;
using Xunit;

namespace PaymentIntegration.Tests;

public sealed class DemoConsoleTests
{
    [Theory]
    [InlineData("1", "approval")]
    [InlineData("5", "out-of-order")]
    [InlineData("12", "reversal-then-refund")]
    public async Task A_number_selects_its_scenario_and_returns_to_the_menu(string choice, string expected)
    {
        using var input = new StringReader(choice + "\n0\n");
        using var output = new StringWriter();
        var selected = new List<string>();

        var result = await DemoConsole.RunAsync(input, output, (name, _) =>
        {
            selected.Add(name);
            return Task.FromResult(0);
        }, CancellationToken.None);

        Assert.Equal(0, result);
        Assert.Equal([expected], selected);
        Assert.Contains($"{choice,2}. {expected} - ", output.ToString());
        Assert.Equal(2, output.ToString().Split("Select scenario: ").Length - 1);
    }

    [Fact]
    public async Task Invalid_choices_are_reprompted_without_running_a_scenario()
    {
        using var input = new StringReader("hello\n-1\n13\n\n1\n0\n");
        using var output = new StringWriter();
        var selected = new List<string>();

        await DemoConsole.RunAsync(input, output, (name, _) =>
        {
            selected.Add(name);
            return Task.FromResult(0);
        }, CancellationToken.None);

        Assert.Equal(["approval"], selected);
        Assert.Equal(4, output.ToString().Split("Enter a number from 1 to 12, or 0 to exit.").Length - 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0\n")]
    [InlineData(" Q \n")]
    public async Task Exit_or_closed_input_finishes_without_starting_a_scenario(string text)
    {
        using var input = new StringReader(text);
        using var output = new StringWriter();

        var result = await DemoConsole.RunAsync(input, output,
            (_, _) => throw new InvalidOperationException("No scenario should run."), CancellationToken.None);

        Assert.Equal(0, result);
    }

    [Fact]
    public async Task The_next_scenario_waits_for_the_current_run_to_complete()
    {
        using var input = new StringReader("1\n2\n0\n");
        using var output = new StringWriter();
        var selected = new List<string>();
        var completion = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var session = DemoConsole.RunAsync(input, output, (name, _) =>
        {
            selected.Add(name);
            started.TrySetResult();
            return name == "approval" ? completion.Task : Task.FromResult(0);
        }, CancellationToken.None);

        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(session.IsCompleted);
        Assert.Equal(["approval"], selected);
        completion.SetResult(0);
        Assert.Equal(0, await session);
        Assert.Equal(["approval", "decline"], selected);
    }

    [Fact]
    public async Task A_failed_run_allows_another_choice_and_keeps_a_nonzero_exit_code()
    {
        using var input = new StringReader("1\n2\n0\n");
        using var output = new StringWriter();
        var selected = new List<string>();

        var result = await DemoConsole.RunAsync(input, output, (name, _) =>
        {
            selected.Add(name);
            return Task.FromResult(name == "approval" ? 1 : 0);
        }, CancellationToken.None);

        Assert.Equal(["approval", "decline"], selected);
        Assert.Equal(1, result);
    }

    [Fact]
    public async Task Cancellation_stops_the_menu_without_starting_another_run()
    {
        using var input = new StringReader("1\n2\n");
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var selected = new List<string>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DemoConsole.RunAsync(input, output, (name, token) =>
        {
            Assert.Equal(cancellation.Token, token);
            selected.Add(name);
            cancellation.Cancel();
            return Task.FromResult(0);
        }, cancellation.Token));

        Assert.Equal(["approval"], selected);
    }

    [Fact]
    public async Task Cancellation_interrupts_waiting_for_console_input()
    {
        using var input = new BlockingReader();
        using var output = new StringWriter();
        using var cancellation = new CancellationTokenSource();
        var session = Task.Run(() => DemoConsole.RunAsync(input, output,
            (_, _) => throw new InvalidOperationException("No scenario should run."), cancellation.Token));
        try
        {
            await input.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            input.Completion.TrySetResult(null);
        }
    }

    private sealed class BlockingReader : TextReader
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override string? ReadLine()
        {
            Started.TrySetResult();
            return Completion.Task.GetAwaiter().GetResult();
        }
    }
}

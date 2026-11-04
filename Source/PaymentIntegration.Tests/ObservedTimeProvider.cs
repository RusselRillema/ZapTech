using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace PaymentIntegration.Tests;

// Observe timer registration so tests can advance time without racing async continuations.
internal sealed class ObservedTimeProvider : TimeProvider
{
    private readonly FakeTimeProvider clock = new();
    private readonly Channel<TimeSpan> timers = Channel.CreateUnbounded<TimeSpan>();

    public override DateTimeOffset GetUtcNow() => clock.GetUtcNow();
    public override long GetTimestamp() => clock.GetTimestamp();
    public override long TimestampFrequency => clock.TimestampFrequency;

    public override ITimer CreateTimer(
        TimerCallback callback,
        object? state,
        TimeSpan dueTime,
        TimeSpan period)
    {
        var timer = clock.CreateTimer(callback, state, dueTime, period);
        timers.Writer.TryWrite(dueTime);
        return timer;
    }

    public void Advance(TimeSpan elapsed) => clock.Advance(elapsed);

    public async Task WaitForTimerAsync(TimeSpan dueTime)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        while (true)
        {
            var registered = await timers.Reader.ReadAsync(timeout.Token);

            if (registered == dueTime)
            {
                return;
            }
        }
    }
}

namespace AcmePay.Stub;

// Replaces the defaults for newly accepted payments in one dedicated demo stub.
public sealed record DemoScenario(
    string PaymentOutcome = "Approved",
    int ResponseDelaySeconds = 0,
    bool HoldCallbacks = false);

public sealed class DemoScenarioState
{
    private DemoScenario _current = new();

    public DemoScenario Current => Volatile.Read(ref _current);

    public string? Configure(DemoScenario? scenario)
    {
        if (scenario is null)
            return "A scenario object is required.";
        if (scenario.PaymentOutcome is not ("Approved" or "Declined"))
            return "PaymentOutcome must be Approved or Declined.";
        if (scenario.ResponseDelaySeconds is < 0 or > 60)
            return "ResponseDelaySeconds must be between 0 and 60.";

        Volatile.Write(ref _current, scenario);
        return null;
    }
}

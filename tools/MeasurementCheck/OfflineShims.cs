namespace SoncaAudioInspector;

// Offline stand-ins for the response-limit types in src/Measurements/ResponseLimitPolicy.cs and MeasurementCriteria.cs.
// Those files extend TestConfig, whose main part lives in the WPF shell, so they cannot be linked into this pure
// net9.0 harness. SpeakerMeasurementDiagnosis only reads the members below; the checks here never supply limit data
// (ResponseLimits/ResponseAssessment stay null), so nothing in this file is evaluated. Dip-window and limit behaviour
// is verified against the real assembly by AudioLifecycleCheck --brief-phase=P1/P2 and ConfigAcceptanceCheck.
// A new member used by the linked sources fails this build on purpose.
public readonly record struct FrequencyEvaluationRange(double MinimumHz, double MaximumHz)
{
    public bool Contains(double frequency) => double.IsFinite(frequency) && frequency >= MinimumHz && frequency <= MaximumHz;
}

public sealed record ResponseLimitSettings(FrequencyEvaluationRange Range);

public sealed record ResponseLimitPoint(double FrequencyHz, bool Outside, bool CountInQuota);

public sealed record ResponseLimitAssessment(IReadOnlyList<ResponseLimitPoint> Points)
{
    public IReadOnlyList<string> IssueSummaries() => [];
}

internal static class TestRunner
{
    public const double BassMin = 20.0;
    public const double BassMax = 250.0;
    public const double MidMin = BassMax;
    public const double MidMax = 4000.0;
    public const double TrebleMin = MidMax;
    public const double TrebleMax = 20000.0;
}

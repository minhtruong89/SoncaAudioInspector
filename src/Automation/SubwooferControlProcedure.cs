namespace SoncaAudioInspector;

public enum SubwooferControl { None, Crossover, Delay, Phase }

/// <summary>Where the operator must leave each rear-panel control before a step is measured.</summary>
public sealed record SubwooferPanelState(double CrossoverHz, double DelayMs, bool Phase180);

/// <summary>Changed: the control this step judges. LedQuestion: the operator also reports the 180° LED as lit or off
/// (absolute state, the same question on every step; "no LED" is accepted).</summary>
public sealed record SubwooferStep(string Id, string Title, string Instruction, SubwooferPanelState Panel,
    SubwooferControl Changed, string? LedQuestion = null);

/// <summary>One thing the operator must physically do before a step (a step may need several).</summary>
public sealed record SubwooferAction(SubwooferControl Control, string Text);

public static class SubwooferControlProcedure
{
    public const string Baseline = "baseline", CrossoverMin = "crossover-min", DelayMax = "delay-max", Phase180 = "phase-180", Return = "return";
    public const string LedQuestionText = "Đèn 180° bây giờ đang SÁNG hay TẮT?";
    public static readonly IReadOnlyList<SubwooferStep> Steps =
    [
        new(Baseline, "1 · Vị trí chuẩn",
            "① Vặn CROSSOVER FREQUENCY hết cỡ về 180 Hz. ② Vặn TIME ALIGNING DELAY hết cỡ về 0 ms. ③ PHASE ở 0° (đèn 180° TẮT). "
            + "Đặt mic sát giữa màng loa sub (1–2 cm); giữ nguyên mic, âm lượng, vị trí loa đến hết bài.",
            new(180, 0, false), SubwooferControl.None),
        new(CrossoverMin, "2 · Crossover 40 Hz",
            "① Chỉ vặn CROSSOVER FREQUENCY hết cỡ về 40 Hz. Giữ DELAY 0 ms, PHASE 0°.",
            new(40, 0, false), SubwooferControl.Crossover),
        new(DelayMax, "3 · Delay 20 ms",
            "① Vặn CROSSOVER về lại 180 Hz (hết cỡ). ② Vặn TIME ALIGNING DELAY hết cỡ về 20 ms. PHASE giữ 0°.",
            new(180, 20, false), SubwooferControl.Delay),
        new(Phase180, "4 · Phase 180°",
            "① Vặn DELAY về lại 0 ms. ② BẤM nút PHASE một lần. CROSSOVER giữ 180 Hz.",
            new(180, 0, true), SubwooferControl.Phase, LedQuestionText),
        new(Return, "5 · Trả về & xác nhận",
            "① BẤM nút PHASE lần nữa để về 0°. Giữ CROSSOVER 180 Hz, DELAY 0 ms.",
            new(180, 0, false), SubwooferControl.Phase, LedQuestionText),
    ];

    /// <summary>Every control whose target differs from the previous step (all three on step 1).</summary>
    public static IReadOnlyList<SubwooferAction> Actions(int index)
    {
        var target = Steps[index].Panel;
        var previous = index == 0 ? null : Steps[index - 1].Panel;
        var actions = new List<SubwooferAction>();
        if (previous == null || previous.CrossoverHz != target.CrossoverHz)
            actions.Add(new(SubwooferControl.Crossover, $"VẶN CROSSOVER → {target.CrossoverHz:0} Hz"));
        if (previous == null || previous.DelayMs != target.DelayMs)
            actions.Add(new(SubwooferControl.Delay, $"VẶN DELAY → {target.DelayMs:0} ms"));
        if (previous == null) actions.Add(new(SubwooferControl.Phase, "PHASE 0° · đèn 180° TẮT"));
        else if (previous.Phase180 != target.Phase180)
            actions.Add(new(SubwooferControl.Phase, $"BẤM PHASE → đèn 180° {(target.Phase180 ? "SÁNG" : "TẮT")}"));
        return actions;
    }
}

/// <summary>Guided session state. Responses must all come from the route/levels of step 1.</summary>
public sealed class SubwooferControlSession
{
    private readonly Dictionary<string, SubwooferResponse> _responses = new();
    private readonly Dictionary<string, bool?> _led = new();
    public SubwooferControlSession(SubwooferSweepSettings? settings = null, SubwooferControlLimits? limits = null)
    {
        Settings = settings ?? new(); Settings.Validate(); Limits = limits ?? new();
    }
    public SubwooferSweepSettings Settings { get; }
    public SubwooferControlLimits Limits { get; }
    public IReadOnlyList<SubwooferStep> Steps => SubwooferControlProcedure.Steps;
    public int CurrentIndex { get; private set; }
    public SubwooferStep? Current => CurrentIndex < Steps.Count ? Steps[CurrentIndex] : null;
    public bool IsComplete => CurrentIndex >= Steps.Count;
    /// <summary>Last attempt of the current step when it was rejected (shown to the operator; step not advanced).</summary>
    public SubwooferResponse? LastRejected { get; private set; }
    public SubwooferResponse? Response(string stepId) => _responses.GetValueOrDefault(stepId);
    public bool? Led(string stepId) => _led.GetValueOrDefault(stepId);
    public IEnumerable<(SubwooferStep Step, SubwooferResponse Response)> Measured
        => Steps.Where(s => _responses.ContainsKey(s.Id)).Select(s => (s, _responses[s.Id]));

    /// <summary>Stores a valid response for the current step and advances; ledLit is the reported 180° LED state (true = lit).
    /// Returns false (step kept) when rejected.</summary>
    public bool Record(SubwooferResponse response, bool? ledLit = null)
    {
        var step = Current ?? throw new InvalidOperationException("Bài test đã xong; bấm Làm lại để đo lại.");
        if (step.LedQuestion != null && ledLit == null && !NoLedAnswered)
            throw new InvalidOperationException("Chọn trạng thái đèn 180° trước khi đo bước này.");
        var baseline = Response(SubwooferControlProcedure.Baseline);
        if (response.IsValid && step.Id != SubwooferControlProcedure.Baseline && baseline != null && baseline.RouteKey != response.RouteKey)
            response = response with { IsValid = false, Reason = "Ngõ, rate hoặc âm lượng Windows đã đổi so với bước 1; làm lại từ đầu." };
        if (!response.IsValid) { LastRejected = response; return false; }
        LastRejected = null; _responses[step.Id] = response;
        if (step.LedQuestion != null) _led[step.Id] = ledLit;
        if (step.Id == SubwooferControlProcedure.Baseline)
            foreach (var later in Steps.Skip(1)) { _responses.Remove(later.Id); _led.Remove(later.Id); }
        // After re-measuring a middle step, continue at the first step that still has no result.
        int next = CurrentIndex + 1;
        while (next < Steps.Count && _responses.ContainsKey(Steps[next].Id)) next++;
        CurrentIndex = next;
        return true;
    }

    /// <summary>The operator said the unit has no 180° LED; LED answers are then not required.</summary>
    public bool NoLedAnswered { get; set; }

    /// <summary>Re-measure a step. Going back to step 1 discards everything measured after it.</summary>
    public void GoTo(int index)
    {
        if (index < 0 || index >= Steps.Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (index > 0 && Response(SubwooferControlProcedure.Baseline) == null) throw new InvalidOperationException("Đo bước 1 trước.");
        CurrentIndex = index; LastRejected = null;
    }

    public void Reset() { _responses.Clear(); _led.Clear(); CurrentIndex = 0; LastRejected = null; NoLedAnswered = false; }

    public IReadOnlyList<SubwooferCheck> Checks
    {
        get
        {
            var b = Response(SubwooferControlProcedure.Baseline);
            SubwooferCheck Pending(string name, string step) => new(name, SubwooferControlEvaluation.Pending, $"Chờ đo bước {step}.");
            SubwooferCheck Of(string name, string id, string label, Func<SubwooferResponse, SubwooferResponse, SubwooferCheck> check)
                => b != null && Response(id) is { } r ? check(b, r) : Pending(name, b == null ? "1" : label);
            return
            [
                Of("Crossover", SubwooferControlProcedure.CrossoverMin, "2", (x, y) => SubwooferControlEvaluation.Crossover(x, y, Limits)),
                Of("Time aligning delay", SubwooferControlProcedure.DelayMax, "3", (x, y) => SubwooferControlEvaluation.Delay(x, y, Limits)),
                Of("Phase 180°", SubwooferControlProcedure.Phase180, "4", (x, y) => SubwooferControlEvaluation.Phase(x, y, Led(SubwooferControlProcedure.Phase180), Limits)),
                Of("Trả về ban đầu", SubwooferControlProcedure.Return, "5", (x, y) => SubwooferControlEvaluation.Return(x, y,
                    Led(SubwooferControlProcedure.Return) is bool lit ? !lit : null, Limits)),
            ];
        }
    }

    public string Verdict => SubwooferControlEvaluation.Combine(Checks.Select(c => c.Verdict));

    /// <summary>Step relative to step 1 (phase/level curves for the plot); null until both are measured.</summary>
    public SubwooferRelation? RelationToBaseline(string stepId)
        => stepId != SubwooferControlProcedure.Baseline && Response(SubwooferControlProcedure.Baseline) is { } b && Response(stepId) is { } r
            ? SubwooferControlAnalysis.Relate(b, r) : null;
}

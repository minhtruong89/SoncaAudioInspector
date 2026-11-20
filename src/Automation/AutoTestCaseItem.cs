using System.ComponentModel;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed record AudioQaAcquisitionSnapshot(double PlaybackLevelDbfs, int PlaybackSampleRate, int RecordingSampleRate,
	bool ExclusivePlayback, int? PlaybackChannel, int? RecordingChannel, string PlaybackDeviceId, string RecordingDeviceId,
	double ThdLimitPercent, DateTimeOffset CapturedUtc)
{
    public string PlaybackDeviceName { get; init; } = "";
    public string RecordingDeviceName { get; init; } = "";
    public int? PlaybackEndpointSampleRate { get; init; }
    public int? PlaybackDefaultSampleRate { get; init; }
    public int? RecordingDefaultSampleRate { get; init; }
    public bool SampleRateContinuationAccepted { get; init; }
    public AudioQaAcquisitionSnapshot WithSampleRates(MeasurementSampleRates? rates, bool accepted)
        => rates == null || rates.Playback.Id != PlaybackDeviceId || rates.Recording.Id != RecordingDeviceId ? this
            : this with { PlaybackEndpointSampleRate = rates.Playback.StreamRate, PlaybackDefaultSampleRate = rates.Playback.DefaultRate,
                RecordingDefaultSampleRate = rates.Recording.DefaultRate, SampleRateContinuationAccepted = accepted && rates.MismatchKeys().Any() };
}

public class AutoTestCaseItem : INotifyPropertyChanged
{
	/// <summary>Owner 2026-11-19 (A2): neutral gray brush for optional tests (e.g. IO009), never red/green.</summary>
	public static readonly SolidColorBrush OptionalBrush = CreateFrozenBrush(161, 161, 170);
	public static readonly SolidColorBrush DefaultWaitingBrush = CreateFrozenBrush(113, 113, 122);

	private static SolidColorBrush CreateFrozenBrush(byte r, byte g, byte b)
	{
		var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
		brush.Freeze();
		return brush;
	}

	private string _status = "WAITING";

	private Brush _statusBrush = DefaultWaitingBrush;

	private string _freqStatus = "ĐANG CHỜ";

	private Brush _freqBrush = DefaultWaitingBrush;

	private string _thdStatus = "ĐANG CHỜ";

	private Brush _thdBrush = DefaultWaitingBrush;

	public string Id { get; set; } = "";

	public string Name { get; set; } = "";
	public string DisplayName => Id + " — " + Name;
    public string RouteText => AudioQaRouteDetails.Route(this, RouteModel);
    internal InOutConfig? RouteModel { get; set; }
	public string QualityLabel => Config.SpeakerStage == "MicEffects" ? "• Effect: " : Config.SpeakerStage == "MicFrequencyShift" ? "• Di tần: " : "• Distortion sweep: ";

	public TestConfig Config { get; set; } = new TestConfig();

	public string NoiseStatus { get; set; } = "CHƯA ĐO";

	private IReadOnlyList<ConfiguredTestResult> _additionalResults = Array.Empty<ConfiguredTestResult>();
	public IReadOnlyList<ConfiguredTestResult> AdditionalResults
	{
		get => _additionalResults;
		set { _additionalResults = value; OnPropertyChanged(nameof(AdditionalResults)); OnPropertyChanged(nameof(AdditionalStatus)); OnPropertyChanged(nameof(AdditionalBrush)); }
	}
	public string AdditionalStatus => Config.SpeakerStage is "MicEffects" or "MicFrequencyShift" ? "" : AdditionalResults.Count > 0 ? string.Join(Environment.NewLine, AdditionalResults.Select(r => r.Display))
		: string.Join(Environment.NewLine, new[] { (Config.TestEffect, "Effect"), (Config.TestAirLeak, "Rò khí"), (Config.TestLooseParticle, "Hạt rác"), (Config.TestSoundLevel, "Mức âm") }
			.Concat(AdditionalTestRegistry.GetEnabled(Config).Select(t => (true, t.Metadata.DisplayName)))
				.Where(p => p.Item1).Select(p => p.Item2 + ": CHƯA ĐO"));
	public Brush AdditionalBrush => AdditionalResults.Any(r => r.Status == "FAIL") ? Brushes.Salmon
		: AdditionalResults.Count == 0 || AdditionalResults.Any(r => !r.Passed) ? Brushes.Gold : Brushes.MediumSpringGreen;

	public string DistortionText { get; set; } = "CHƯA CÓ DỮ LIỆU DISTORTION";

	public string ResponseLevelUnit { get; set; } = "dBFS";

    private AudioQaAcquisitionSnapshot? _acquisition;
    public AudioQaAcquisitionSnapshot? Acquisition
    {
        get => _acquisition;
        set { _acquisition = value; OnPropertyChanged(nameof(RouteText)); }
    }

	public double[] ResponseFrequencies { get; set; } = Array.Empty<double>();

	public double[] ResponseLevels { get; set; } = Array.Empty<double>();
	public IReadOnlyDictionary<double, double> UnsmoothedResponse { get; set; } = new Dictionary<double, double>();
	public IReadOnlyDictionary<double, double> UnsmoothedStandardCurve { get; set; } = new Dictionary<double, double>();
	[System.Text.Json.Serialization.JsonIgnore]
	public StandardAcousticResult? ImpulseResult { get; set; }
	[System.Text.Json.Serialization.JsonIgnore]
	public FrequencyShiftResult? FrequencyShift { get; set; }
	[System.Text.Json.Serialization.JsonIgnore]
	public FeedbackGainResult? FeedbackGain { get; set; }
	private string _optionalMeasurementSummary = "";
	public string OptionalMeasurementSummary
	{
		get => _optionalMeasurementSummary;
		set { _optionalMeasurementSummary = value ?? ""; OnPropertyChanged(nameof(OptionalMeasurementSummary)); OnPropertyChanged(nameof(StatusDisplay)); }
	}
	[System.Text.Json.Serialization.JsonIgnore]
	public RubBuzzMetrics? RubBuzzMetrics { get; set; }
	[System.Text.Json.Serialization.JsonIgnore]
	public FrequencySpectrum? RubBuzzSpectrum { get; set; }
	[System.Text.Json.Serialization.JsonIgnore]
	public IReadOnlyList<MeasurementValue> MeasurementValues { get; set; } = Array.Empty<MeasurementValue>();

	public IReadOnlyDictionary<double, double> StandardCurve { get; set; } = new Dictionary<double, double>();

	public IReadOnlyDictionary<double, FrequencyLimitPoint> FrequencyLimits { get; set; }
		= new Dictionary<double, FrequencyLimitPoint>();

	public CriticalFrequencyZone[] CriticalZones { get; set; } = Array.Empty<CriticalFrequencyZone>();
	public ResponseLimitSettings? ResponseLimits { get; set; }
	public ResponseLimitAssessment? ResponseAssessment { get; set; }
    public string SingleRunEarlyPassSummary { get; set; } = "";

	public bool BassPassed { get; set; }

	public bool MidPassed { get; set; }

	public bool TreblePassed { get; set; }

	public bool SilentInputDetected { get; set; }

	public bool FeqRepeatabilityInvalid { get; set; }

	public IReadOnlyDictionary<double, ToneQualityMetrics> DistortionMeasurements { get; set; }
		= new Dictionary<double, ToneQualityMetrics>();

	public string Status
	{
		get
		{
			return _status;
		}
		set
		{
			_status = value;
			OnPropertyChanged("Status");
			OnPropertyChanged("StatusDisplay");
		}
	}

	public string StatusDisplay => _status switch
	{
		"WAITING" => "ĐANG CHỜ", 
		"RUNNING" => "ĐANG ĐO", 
		_ when Config?.IsOptional == true => string.IsNullOrWhiteSpace(_optionalMeasurementSummary)
			? "TÙY CHỌN"
			: "TÙY CHỌN · " + _optionalMeasurementSummary,
		"PASS" => "ĐẠT", 
		"FAIL" => "KHÔNG ĐẠT", 
		"RETRY" => "TÍN HIỆU CHƯA ỔN ĐỊNH",
		"INVALID" => "ĐO KHÔNG HỢP LỆ",
		"NO_SIGNAL" => "KHÔNG CÓ TÍN HIỆU",
		"INCONCLUSIVE" => "CHƯA KẾT LUẬN",
        "REVIEW" => "CẦN DUYỆT",
		_ => _status, 
	};

	public Brush StatusBrush
	{
		get
		{
			return _statusBrush;
		}
		set
		{
			_statusBrush = value;
			OnPropertyChanged("StatusBrush");
		}
	}

	public string FreqStatus
	{
		get
		{
			return _freqStatus;
		}
		set
		{
			_freqStatus = value;
			OnPropertyChanged("FreqStatus");
		}
	}

	public Brush FreqBrush
	{
		get
		{
			return _freqBrush;
		}
		set
		{
			_freqBrush = value;
			OnPropertyChanged("FreqBrush");
		}
	}

	public string ThdStatus
	{
		get
		{
			return _thdStatus;
		}
		set
		{
			_thdStatus = value;
			OnPropertyChanged("ThdStatus");
		}
	}

	public Brush ThdBrush
	{
		get
		{
			return _thdBrush;
		}
		set
		{
			_thdBrush = value;
			OnPropertyChanged("ThdBrush");
		}
	}

	public event PropertyChangedEventHandler? PropertyChanged;

	protected void OnPropertyChanged(string name)
	{
		PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
	}
}

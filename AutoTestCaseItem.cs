using System.ComponentModel;
using System.Windows.Media;

namespace SoncaAudioInspector;

public sealed record AudioQaAcquisitionSnapshot(double PlaybackLevelDbfs, int PlaybackSampleRate, int RecordingSampleRate,
	bool ExclusivePlayback, int? PlaybackChannel, int? RecordingChannel, string PlaybackDeviceId, string RecordingDeviceId,
	double ThdLimitPercent, DateTimeOffset CapturedUtc);

public class AutoTestCaseItem : INotifyPropertyChanged
{
	private string _status = "WAITING";

	private Brush _statusBrush = new SolidColorBrush(Color.FromRgb(113, 113, 122));

	private string _freqStatus = "ĐANG CHỜ";

	private Brush _freqBrush = new SolidColorBrush(Color.FromRgb(113, 113, 122));

	private string _thdStatus = "ĐANG CHỜ";

	private Brush _thdBrush = new SolidColorBrush(Color.FromRgb(113, 113, 122));

	public string Id { get; set; } = "";

	public string Name { get; set; } = "";

	public TestConfig Config { get; set; } = new TestConfig();

	public string NoiseStatus { get; set; } = "CHƯA ĐO";

	public string DistortionText { get; set; } = "CHƯA CÓ DỮ LIỆU DISTORTION";

	public string ResponseLevelUnit { get; set; } = "dBFS";

	public AudioQaAcquisitionSnapshot? Acquisition { get; set; }

	public double[] ResponseFrequencies { get; set; } = Array.Empty<double>();

	public double[] ResponseLevels { get; set; } = Array.Empty<double>();

	public IReadOnlyDictionary<double, double> StandardCurve { get; set; } = new Dictionary<double, double>();

	public IReadOnlyDictionary<double, FrequencyLimitPoint> FrequencyLimits { get; set; }
		= new Dictionary<double, FrequencyLimitPoint>();

	public CriticalFrequencyZone[] CriticalZones { get; set; } = Array.Empty<CriticalFrequencyZone>();

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
		"PASS" => "ĐẠT", 
		"FAIL" => "KHÔNG ĐẠT", 
		"RETRY" => "TÍN HIỆU CHƯA ỔN ĐỊNH",
		"INVALID" => "ĐO KHÔNG HỢP LỆ",
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

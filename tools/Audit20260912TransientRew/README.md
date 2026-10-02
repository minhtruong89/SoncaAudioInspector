# Numerical audit probes — 2026-09-12

This console harness probes the current analyzers with synthetic signals. It does
not access audio devices or change application settings. It is a focused
verification harness, not a passing/failing production suite: process exit code
0 only means the probes ran, not that the measurement algorithms are correct.

## Reproduce

Run from the repository root. Rebuild both source-linked check projects first:
this harness references their Debug assemblies, so skipping that step can test
stale code. The .NET 9 SDK and restored MathNet.Numerics 5.0.0 package are needed.

```powershell
dotnet run --project tools\StandardMeasurementCheck\StandardMeasurementCheck.csproj
dotnet run --project tools\MeasurementCheck\MeasurementCheck.csproj
dotnet run --project tools\Audit20260912TransientRew\Audit20260912TransientRew.csproj
```

On the audit machine the first two suites were rebuilt and passed. The focused
probes below nevertheless exposed cases not covered by those suites. These are
not hardware measurements or a side-by-side numerical comparison with REW.

## Observed output

```text
INVALID input: pink NaN=INVALID_NONFINITE_DATA; RTA NaN=INVALID_NONFINITE_DATA; IMD NaN=INVALID_NONFINITE_DATA
WRONG stimulus: white noise rejected as IMD=INVALID_STIMULUS_NOT_DETECTED
HARMONIC probe: expected THD=1%, SFDR=40 dBc; actual THD=1.00007%, SFDR=40.63 dB
TRANSIENT same burst at 0.05/0.50/0.95s: RubBuzz=0.748456% at all positions; one event at each position; transient peak=-13.47 dBc
TIME VIEWS: input peak=1 at t=0; returned impulse peak=1.000, ETC peak=0.0 dB
DECAY known RT60=1.2s, captured=3.0s: T20=1.200s, valid=True, R2=1.0000
DECAY known RT60=12.0s, captured=3.0s: T20=5.628s, valid=False, R2=0.9662
ARRIVAL positive direct=20ms, stronger inverted reflection=50ms: reported=20.000ms, polarity=NORMAL, validity=DIAGNOSTIC, directPathClaim=True
ESS nonlinear y=x+0.5x^2: H2@1k=-26.03 dBc, H3=-165.49 dBc, THD=4.997%, points=111
```

## Interpretation and source entry points

- `AdvancedAudioMeasurement.AnalyzeTone`: the nominal THD calculation works and
  SFDR now includes the deliberately dominant H2 spur.
- `AdvancedAudioMeasurement.AnalyzeRubBuzz`: energy and event detection now use
  the whole capture. The identical 5 ms burst has the same level and is found at
  all three positions; the result carries its timestamp, duration, crest and phase.
- `AnalyzePinkNoise`, `AnalyzeRtaSpectrum`, `AnalyzeSmpteImd`: non-finite input
  is rejected. IMD also requires evidence that both requested tones exist.
- `StandardAcousticMeasurement.BuildTimeDomainViews`: display reduction now
  retains the largest absolute impulse sample in each output bucket.
- `BuildEnergyDecayCurve` / `FitDecay`: R-squared is no longer sufficient. A fit
  is rejected when capture duration is less than 1.2 times estimated RT60.
- `AnalyzeLogSweep`: the arrival detector now chooses the first prominent local
  peak, so the stronger later reflection no longer changes arrival or polarity.

## Additional source/documentation checks

- Sonca's default SMPTE calculation now uses the documented second- and third-
  order DIN/SMPTE components. THD+N now uses a total-input reference. Bandwidth,
  windowing and calibration must still match before comparing numbers with REW.
- Current response calibration is not absolute SPL calibration. Pink/RTA/IMD
  do not consume the microphone response correction used by the sweep path.
- `STANDARD_MEASUREMENT_GUIDE.vi.md` and the in-app guide now cover only current
  controls and state which measured values support each diagnostic inference.

## Primary references used for definitions and feasibility

- [REW RTA and distortion definitions](https://www.roomeqwizard.com/betahelp/help/html/spectrum.html)
- [REW RT60 interpretation](https://www.roomeqwizard.com/help/help/html/graph_rt60.html)
- [REW impulse and timing](https://www.roomeqwizard.com/help/help_en-GB/html/graph_impulse.html)
- [Analog Devices SFDR definition](https://www.analog.com/en/resources/glossary/sfdr.html)
- [Klippel: Rub and Buzz Detection without Golden Unit](https://www.klippel.de/fileadmin/user_upload/AN_22_Rub_and_Buzz_Detection_without_Golden_Unit.pdf)

Time-localized residual peak/envelope detection is implemented alongside the
spectral diagnostic, with event timestamp, duration, crest and phase. It does
not need a Golden speaker. Thresholds,
ambient-noise rejection and actual defect sensitivity still require microphone
recordings and hardware validation; synthetic probes cannot establish those.

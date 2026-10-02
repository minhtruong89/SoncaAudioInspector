# Measurement audit probes, 2026-09-09

These diagnostic probes call the current production analyzers with synthetic inputs. They print observations, not acceptance-test PASS assertions. They do not access audio devices or change production algorithms.

Run from the repository root:

```powershell
dotnet run --project tools\Audit20260909\Audit20260909.csproj -c Release --artifacts-path tools\Audit20260909\artifacts -p:NuGetAudit=false
```

Observed on 2026-09-09:

- 12 kHz at 48 kHz sample rate: `IsValid=True`, no measured harmonics, `THD=0%`. Unavailable harmonic distortion must not be represented as a measured zero.
- Pure 1003.21 Hz sine, expected 1000 Hz: estimated 1003.208125 Hz, reported THD+N 0.2721%.
- Pure 6997.3 Hz sine, expected 7000 Hz: estimated 6997.296250 Hz, reported THD+N 0.5441%. Frequency fitting error contributes to the residual.
- White noise supplied to the pink analyzer is accepted as `DIAGNOSTIC`; the analyzer does not establish that the DUT emitted the requested stimulus.
- Identical noise reduced by 20 dB still compares as `PASS_DIAGNOSTIC` because the pink comparison uses normalized shape only.
- A NaN sample is accepted by Pink and RTA as `DIAGNOSTIC`; Pink comparison against finite golden data returns `PASS_DIAGNOSTIC`.
- White noise supplied to IMD is accepted as `DIAGNOSTIC`, reporting IMD 441.72%.
- A sine clipped at ADC full scale and then scaled by software gain 0.2 reports peak 0.2 and clipped sample percentage 0. The multi-tone UI's clipping checks cannot detect this clipping from the scaled samples alone.
- The private decay fitter, invoked with a curve spanning only -5 to -10 dB, accepts a requested T20 fit (-5 to -25 dB), reporting 1.2 s and R-squared 1. This isolates the missing fit-span validation; it is not an end-to-end acoustic capture.

Existing StandardMeasurementCheck and MeasurementCheck passed. FeedbackShiftCheck passed 37 checks using a separate artifacts directory after the existing obj directory encountered a permissions error.

No paired physical Sonca/REW measurement, microphone calibration validation, or visual popup rendering test was performed. Source inspection establishes UI wiring and message content only.

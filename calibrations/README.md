# Microphone KT USB calibration

`99-00192.txt` is the existing frequency-response calibration for the physical KT USB microphone with serial **99-00192**. Keep this file unchanged with the source. The project copies `calibrations/` into build and publish output automatically.

Build on another Windows computer with .NET 9 SDK:

```powershell
dotnet build SoncaAudioInspector.csproj -c Debug
```

Select the KT USB recording endpoint in Audio Routing. The app loads `calibrations/99-00192.txt` beside the executable first, without relying on a Downloads folder or a path from the original computer. The detailed measurement window (`Ctrl+Shift+I`) shows the calibration filename. FastTrack electrical loopback does not use the microphone correction.

The profile corrects the frequency response of this particular microphone. A different KT microphone needs its own profile; copying this profile does not calibrate every KT microphone. Absolute dB SPL also requires a valid sensitivity/SPL calibration and matching gain, orientation, fixture and acoustic conditions. This frequency profile alone does not prove absolute SPL or REW-equivalent accuracy. Distortion calibration remains controlled separately by the measurement option.

Use 44.1 kHz for the standard measurement route, check the actual capture rate, and acquire fresh 1/24-octave Line standards on the destination fixture.

# Offline regression captures

`manifest.json` records provenance, original WAV SHA-256 and decoded first-channel SHA-256 for the 28 historical captures.
Each `*.wav.f32.gz` contains lossless little-endian IEEE float samples from channel 1 at 48000 Hz, exactly the samples
consumed by the existing replay checks. No microphone or sound output is accessed. These are historical recordings,
not new physical qualification. The normal check command reads these repository fixtures; it does not read old artifacts.

The original recordings were decoded from RIFF PCM/IEEE-float (including extensible formats), retaining channel 1 with
the same PCM normalization as NAudio. Compression preserves every decoded sample. No DSP, resampling or gain was applied.

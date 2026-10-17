namespace SoncaAudioInspector;

public sealed record ImpulseEffectSettings
{
    public bool? ExpectEcho { get; init; }
    public bool? ExpectReverb { get; init; }
    public double? ExpectedEchoDelayMs { get; init; }
    public double EchoDelayToleranceMs { get; init; } = 10;
    public double MinimumEchoDelayMs { get; init; } = 30;
    public double MaximumEchoDelayMs { get; init; } = 600;
    public double MinimumEchoLevelDb { get; init; } = -45;
    public double MinimumEchoProminenceDb { get; init; } = 10;
    public double MinimumDiffuseTailDb { get; init; } = -40;
    public double MinimumTailOccupancy { get; init; } = .60;
    public double TailStartMs { get; init; } = 50;
    public double TailEndMs { get; init; } = 1000;
    public double CaptureTailSeconds { get; init; } = 3;

    public void Validate()
    {
        double[] values = { EchoDelayToleranceMs, MinimumEchoDelayMs, MaximumEchoDelayMs, MinimumEchoLevelDb,
            MinimumEchoProminenceDb, MinimumDiffuseTailDb, MinimumTailOccupancy, TailStartMs, TailEndMs, CaptureTailSeconds };
        if (values.Any(v => !double.IsFinite(v)) || MinimumEchoDelayMs < 10 || MaximumEchoDelayMs <= MinimumEchoDelayMs
            || MaximumEchoDelayMs > 2000 || EchoDelayToleranceMs <= 0 || MinimumEchoLevelDb > 0 || MinimumEchoLevelDb < -100
            || MinimumEchoProminenceDb < 3 || MinimumTailOccupancy <= 0 || MinimumTailOccupancy > 1
            || TailStartMs < 10 || TailEndMs <= TailStartMs + 100 || TailEndMs > 5000
            || CaptureTailSeconds < 1 || CaptureTailSeconds > 10
            || ExpectedEchoDelayMs is double delay && (!double.IsFinite(delay) || delay < MinimumEchoDelayMs || delay > MaximumEchoDelayMs))
            throw new ArgumentException("Cấu hình ImpulseEffects không hợp lệ: kiểm tra delay, cửa sổ đuôi và ngưỡng dB.");
    }
}

public sealed record EchoTap(double DelayMs, double LevelDb);

public sealed record ImpulseEffectReference(bool EchoDetected, bool ReverbDetected, double? EchoDelayMs,
    double? EchoLevelDb, double DiffuseTailEnergyDb, double TailOccupancy, string SettingsKey)
{
    public static string Key(ImpulseEffectSettings settings) => System.Text.Json.JsonSerializer.Serialize(settings with { ExpectEcho = null, ExpectReverb = null });
    public static ImpulseEffectReference? From(ImpulseEffectResult? result, ImpulseEffectSettings settings) => result is { IsValid: true, TailTruncated: false, Verdict: not "INCONCLUSIVE" }
        ? new(result.EchoDetected, result.ReverbDetected, double.IsFinite(result.EchoDelayMs) ? result.EchoDelayMs : null,
            result.EchoTaps.Count > 0 ? result.EchoTaps.Max(t => t.LevelDb) : null,
            result.DiffuseTailEnergyDb, result.TailOccupancy, Key(settings)) : null;

    public static ImpulseEffectReference? Average(ImpulseEffectReference? first, ImpulseEffectReference? second, double delayToleranceMs)
    {
        if (first == null || second == null || first.SettingsKey != second.SettingsKey
            || first.EchoDetected != second.EchoDetected || first.ReverbDetected != second.ReverbDetected
            || first.EchoDelayMs is double a && second.EchoDelayMs is double b && Math.Abs(a - b) > delayToleranceMs
            || first.EchoLevelDb is double c && second.EchoLevelDb is double d && Math.Abs(c - d) > 6
            || first.ReverbDetected && Math.Abs(first.DiffuseTailEnergyDb - second.DiffuseTailEnergyDb) > 6) return null;
        return first with { EchoDelayMs = (first.EchoDelayMs + second.EchoDelayMs) / 2,
            EchoLevelDb = (first.EchoLevelDb + second.EchoLevelDb) / 2,
            DiffuseTailEnergyDb = (first.DiffuseTailEnergyDb + second.DiffuseTailEnergyDb) / 2,
            TailOccupancy = (first.TailOccupancy + second.TailOccupancy) / 2 };
    }

    public IReadOnlyList<string> Compare(ImpulseEffectResult current, ImpulseEffectSettings settings)
    {
        if (!current.IsValid || current.Verdict == "INCONCLUSIVE" || current.TailTruncated || current.CapturedTailSeconds * 1000 < settings.TailEndMs + 100)
            return new[] { "Chưa đủ dữ liệu để so echo/reverb với line chuẩn." };
        if (SettingsKey != Key(settings)) return new[] { "Cấu hình phân tích đuôi đã đổi; cần lấy lại mẫu chuẩn echo/reverb." };
        var notes = new List<string>();
        if (EchoDetected && !current.EchoDetected) notes.Add("ECHO_MISSING_REFERENCE: thiếu chuỗi echo so với line chuẩn cùng route.");
        if (!EchoDetected && current.EchoDetected) notes.Add("ECHO_ADDED_REFERENCE: xuất hiện chuỗi echo khác line chuẩn.");
        if (ReverbDetected && !current.ReverbDetected) notes.Add("REVERB_MISSING_REFERENCE: thiếu đuôi reverb so với line chuẩn cùng route.");
        if (!ReverbDetected && current.ReverbDetected) notes.Add("REVERB_ADDED_REFERENCE: xuất hiện đuôi liên tục khác line chuẩn.");
        if (EchoDetected && current.EchoDetected && EchoDelayMs is double delay && Math.Abs(current.EchoDelayMs - delay) > settings.EchoDelayToleranceMs)
            notes.Add($"Echo lệch delay {current.EchoDelayMs - delay:+0.0;-0.0} ms so với chuẩn {delay:0.0} ms.");
        if (EchoLevelDb is double level && current.EchoTaps.Count > 0 && Math.Abs(current.EchoTaps.Max(t => t.LevelDb) - level) > 6)
            notes.Add("Mức echo/direct lệch quá 6 dB so với line chuẩn.");
        if (ReverbDetected && current.ReverbDetected && Math.Abs(current.DiffuseTailEnergyDb - DiffuseTailEnergyDb) > 6)
            notes.Add("Năng lượng đuôi ngoài echo/direct lệch quá 6 dB so với line chuẩn.");
        if (notes.Count == 0) notes.Add("Echo/reverb chưa lệch các tiêu chí của line chuẩn (delay và mức tương đối). Giữ nguyên DSP/fixture khi đối chiếu.");
        return notes;
    }
}

public sealed record ImpulseEffectResult(
    bool IsValid, bool EchoDetected, bool ReverbDetected, double EchoDelayMs,
    IReadOnlyList<EchoTap> EchoTaps, double LateEnergyDb, double DiffuseTailEnergyDb,
    double TailOccupancy, double TailDecay60Seconds, double TailFitRSquared,
    double ImpulseNoiseDb, double CapturedTailSeconds, bool TailTruncated,
    string Verdict, IReadOnlyList<string> Findings)
{
    public string Describe()
    {
        static string Number(double value, string format) => double.IsFinite(value) ? value.ToString(format) : "N/A";
        return $"Impulse — {Verdict}: echo {(EchoDetected ? $"{EchoDelayMs:0.0} ms, {EchoTaps.Count} đỉnh" : "chưa thấy chuỗi đỉnh lặp")}; "
            + $"reverb {(ReverbDetected ? "có đuôi liên tục" : "chưa thấy đuôi liên tục")}; "
            + $"E đuôi/direct {Number(LateEnergyDb, "0.0")} dB, E đuôi ngoài echo {Number(DiffuseTailEnergyDb, "0.0")} dB, "
            + $"độ phủ đuôi {TailOccupancy:P0}, T60 đuôi {Number(TailDecay60Seconds, "0.00")} s (R² {Number(TailFitRSquared, "0.00")}). "
            + string.Join(" ", Findings);
    }
}

/// <summary>Analyses the full, ungated IR. Effect detection is distinct from model acceptance.</summary>
public static class ImpulseEffects
{
    public static ImpulseEffectResult Analyze(IReadOnlyList<double> impulse, int sampleRate, int directIndex,
        bool acquisitionValid, ImpulseEffectSettings? settings = null)
    {
        settings ??= new(); settings.Validate();
        ImpulseEffectResult Invalid(string reason) => new(false, false, false, double.NaN, Array.Empty<EchoTap>(),
            double.NaN, double.NaN, 0, double.NaN, double.NaN, double.NaN, 0, false, "INVALID", new[] { reason });
        if (!acquisitionValid || sampleRate < 8000 || directIndex < 0 || directIndex >= impulse.Count || impulse.Any(v => !double.IsFinite(v)))
            return Invalid("Bản thu/impulse không hợp lệ; không kết luận mất echo/reverb.");
        double duration = (impulse.Count - directIndex - 1) / (double)sampleRate;
        if (duration < .25) return Invalid("Đoạn thu sau direct quá ngắn.");
        int block = Math.Max(1, sampleRate / 1000); // 1 ms, preserves narrow echo peaks
        double binMs = block * 1000.0 / sampleRate;
        int AtMs(double milliseconds) => (int)Math.Ceiling(milliseconds / binMs);
        int count = (impulse.Count - directIndex) / block;
        var peaks = new double[count]; var powers = new double[count]; var peakOffsets = new int[count];
        for (int i = 0; i < count; i++)
        for (int j = 0; j < block; j++)
        {
            double sample = impulse[directIndex + i * block + j];
            if (Math.Abs(sample) > peaks[i]) { peaks[i] = Math.Abs(sample); peakOffsets[i] = j; }
            powers[i] += sample * sample / block;
        }
        double directPeak = peaks.Take(AtMs(6)).DefaultIfEmpty(0).Max();
        if (directPeak < 1e-12) return Invalid("Không có direct impulse đủ mạnh.");
        double[] ending = powers.Skip(Math.Max(10, count * 4 / 5)).Order().ToArray();
        double noisePower = ending.Length > 0 ? ending[ending.Length / 2] : 0;
        double noiseDb = 10 * Math.Log10(Math.Max(1e-30, noisePower / (directPeak * directPeak)));
        if (noiseDb > -25) return Invalid("Direct impulse không đủ nổi so với nền/đuôi; cần thu dài hơn hoặc cải thiện SNR.");
        double threshold = Math.Max(directPeak * Math.Pow(10, settings.MinimumEchoLevelDb / 20), Math.Sqrt(noisePower) * 4);
        var candidates = new List<EchoTap>();
        for (int i = Math.Max(AtMs(15), AtMs(settings.MinimumEchoDelayMs)); i + AtMs(15) < count; i++)
        {
            if (peaks[i] < threshold || peaks[i] < peaks[i - 1] || peaks[i] <= peaks[i + 1]) continue;
            double[] background = Enumerable.Range(i - 12, 25).Where(j => Math.Abs(j - i) > 3).Select(j => peaks[j]).Order().ToArray();
            double local = background[background.Length / 2];
            if (20 * Math.Log10(peaks[i] / Math.Max(1e-30, local)) < settings.MinimumEchoProminenceDb) continue;
            candidates.Add(new EchoTap((i * block + peakOffsets[i]) * 1000.0 / sampleRate, 20 * Math.Log10(peaks[i] / directPeak)));
        }
        var taps = new List<EchoTap>(); double delayMs = double.NaN;
        IEnumerable<double> delays = settings.ExpectedEchoDelayMs is double expected ? new[] { expected }
            : candidates.Where(t => t.DelayMs <= settings.MaximumEchoDelayMs).Select(t => t.DelayMs);
        foreach (double delay in delays)
        {
            if (delay < settings.MinimumEchoDelayMs || delay > settings.MaximumEchoDelayMs) continue;
            var sequence = new List<EchoTap>();
            for (int n = 1; n * delay < duration * 1000 - 15 && n <= 30; n++)
            {
                double tolerance = Math.Max(3, Math.Min(settings.EchoDelayToleranceMs, delay * .05));
                EchoTap? tap = candidates.Where(t => Math.Abs(t.DelayMs - n * delay) <= tolerance).OrderByDescending(t => t.LevelDb).FirstOrDefault();
                if (tap != null) sequence.Add(tap);
            }
            if (sequence.Count > taps.Count) { taps = sequence; delayMs = delay; }
        }
        bool echo = taps.Count >= 2;
        if (echo) delayMs = taps.Select((tap, i) => tap.DelayMs / Math.Max(1, Math.Round(tap.DelayMs / delayMs))).Average();
        else { taps.Clear(); delayMs = double.NaN; }
        double directEnergy = powers.Take(AtMs(10)).Sum();
        int tailStart = Math.Max(AtMs(10), AtMs(settings.TailStartMs)), tailEnd = Math.Min(count - 1, AtMs(settings.TailEndMs));
        double tailEnergy = 0, diffuseEnergy = 0; int diffuseCount = 0, above = 0;
        double occupiedThreshold = Math.Max(noisePower * 10, directPeak * directPeak * 1e-6);
        var fit = new List<(double Time, double Db)>();
        for (int i = tailStart; i < tailEnd; i++)
        {
            double energy = Math.Max(0, powers[i] - noisePower); tailEnergy += energy;
            if (echo && taps.Any(t => Math.Abs(t.DelayMs - i * binMs) <= 15)) continue;
            diffuseEnergy += energy; diffuseCount++;
            if (powers[i] > occupiedThreshold) above++;
        }
        // Fit the wet tail rather than a Schroeder fit dominated by the dry spike.
        for (int i = tailStart; i + AtMs(20) < tailEnd; i += AtMs(20))
        {
            if (echo && taps.Any(t => t.DelayMs >= i * binMs - 15 && t.DelayMs <= i * binMs + 35)) continue;
            double power = powers.Skip(i).Take(AtMs(20)).Average();
            if (power > noisePower * 10) fit.Add((i * binMs / 1000, 10 * Math.Log10(power / (directPeak * directPeak))));
        }
        double decay60 = double.NaN, r2 = double.NaN;
        if (fit.Count >= 8 && fit[^1].Time - fit[0].Time >= .15)
        {
            double mx = fit.Average(p => p.Time), my = fit.Average(p => p.Db);
            double slope = fit.Sum(p => (p.Time - mx) * (p.Db - my)) / Math.Max(1e-30, fit.Sum(p => Math.Pow(p.Time - mx, 2)));
            double total = fit.Sum(p => Math.Pow(p.Db - my, 2));
            r2 = 1 - fit.Sum(p => Math.Pow(p.Db - (my + slope * (p.Time - mx)), 2)) / Math.Max(1e-30, total);
            if (slope < 0 && r2 >= .85 && -slope * (fit[^1].Time - fit[0].Time) >= 10) decay60 = -60 / slope;
        }
        double lateDb = 10 * Math.Log10(Math.Max(1e-30, tailEnergy / Math.Max(1e-30, directEnergy)));
        double diffuseDb = 10 * Math.Log10(Math.Max(1e-30, diffuseEnergy / Math.Max(1e-30, directEnergy)));
        double occupancy = diffuseCount > 0 ? above / (double)diffuseCount : 0;
        bool reverb = diffuseDb >= settings.MinimumDiffuseTailDb && occupancy >= settings.MinimumTailOccupancy;
        double endingPower = powers.Skip(Math.Max(AtMs(10), count - AtMs(100))).Average();
        bool noisyTail = noiseDb > Math.Min(-60, settings.MinimumEchoLevelDb - 6);
        bool truncated = endingPower > Math.Max(directPeak * directPeak * 1e-6, noisePower * 4);
        bool longEnough = duration * 1000 >= Math.Max(settings.TailEndMs + 100, 2 * (settings.ExpectedEchoDelayMs ?? settings.MaximumEchoDelayMs) + 100);
        var findings = new List<string>();
        if (settings.ExpectEcho == true && !echo && longEnough && !noisyTail && !truncated) findings.Add("ECHO_MISSING: không thấy chuỗi echo đạt ngưỡng cấu hình.");
        if (settings.ExpectEcho == false && echo) findings.Add("ECHO_UNEXPECTED: thấy chuỗi echo khi cấu hình yêu cầu tắt.");
        if (settings.ExpectReverb == true && !reverb && longEnough && !noisyTail && !truncated) findings.Add("REVERB_MISSING: đuôi liên tục không đạt ngưỡng cấu hình.");
        if (settings.ExpectReverb == false && reverb) findings.Add("REVERB_UNEXPECTED: thấy đuôi liên tục khi cấu hình yêu cầu tắt.");
        string verdict = findings.Count > 0 ? "FAIL" : !longEnough || truncated || noisyTail ? "INCONCLUSIVE"
            : settings.ExpectEcho.HasValue || settings.ExpectReverb.HasValue ? "PASS" : "OBSERVED";
        if (!settings.ExpectEcho.HasValue && !settings.ExpectReverb.HasValue)
            findings.Add("Chưa cấu hình yêu cầu effect; không kết luận mất hiệu ứng hoặc lỗi DSP từ phản xạ phòng.");
        if (!longEnough || truncated) findings.Add("Đuôi thu chưa đủ để kết luận vắng hiệu ứng; tăng CaptureTailSeconds và giữ nguyên route/mức/DSP.");
        if (noisyTail) findings.Add("Nền IR còn cao so với ngưỡng effect; chưa kết luận vắng echo/reverb. Cải thiện SNR và kiểm tra định dạng/clock đường thu.");
        return new(true, echo, reverb, delayMs, taps, lateDb, diffuseDb, occupancy, decay60, r2, noiseDb, duration, truncated, verdict, findings);
    }
}

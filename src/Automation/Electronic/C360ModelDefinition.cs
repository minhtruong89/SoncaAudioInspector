namespace SoncaAudioInspector;

public static class C360ModelDefinition
{
    public const string Profile = "C360Electronic";
    public const string CaptureName = "RECORDING TYPE C", PreviousCaptureName = "TYPE C RECORDING", LegacyCaptureName = "LINE OUT";
    public static void NormalizeCaptureAlias(InOutConfig config)
    {
        if (config.AutomationProfile != Profile) return;
        foreach (string alias in new[] { LegacyCaptureName, PreviousCaptureName })
        {
            if (!config.Devices.Output.TryGetValue(alias, out var device)) continue;
            // An explicitly different route is not an alias and must retain its identity.
            if (config.Devices.Output.TryGetValue(CaptureName, out var existing) && existing != device) continue;
            if (config.Devices.Output.ContainsKey(CaptureName)) config.Devices.Output.Remove(alias);
            else config.Devices.Output = config.Devices.Output.ToDictionary(p => p.Key == alias ? CaptureName : p.Key, p => p.Value);
            foreach (var test in config.Tests.Where(t => t.RecordingIn == alias)) test.RecordingIn = CaptureName;
        }
        if (config.Description is { } description)
            config.Description = description.Replace(PreviousCaptureName, CaptureName).Replace(LegacyCaptureName, CaptureName);
    }
    public static string CaptureNameFor(InOutConfig config) => config.AutomationProfile == Profile ? CaptureName : LegacyCaptureName;
    public static string ReferenceCaptureName(InOutConfig? config, string name)
        => config?.AutomationProfile == Profile && name is CaptureName or PreviousCaptureName ? LegacyCaptureName : name;
    public static string ReferenceRoute(InOutConfig? config, TestConfig test)
        => test.id + "_" + test.PlaybackOut + "_TO_" + ReferenceCaptureName(config, test.RecordingIn);
    public static void NormalizeReferenceCapture(System.Text.Json.Nodes.JsonObject acquisition, InOutConfig? config, TestConfig test)
    {
        // This is a display-only alias. Canonicalize before hashing so every existing C360 LINE OUT standard keeps its key.
        // Do not rewrite the live configuration or normalize ACOUSTIC 3 / arbitrary recording routes.
        if (config?.AutomationProfile == Profile && test.RecordingIn is CaptureName or PreviousCaptureName)
            acquisition["Recording In"] = LegacyCaptureName;
    }
    public static string ReferenceConfiguration(InOutConfig? config)
    {
        string json = System.Text.Json.JsonSerializer.Serialize(config);
        if (config?.AutomationProfile != Profile || !(config.Devices.Output.ContainsKey(CaptureName) || config.Devices.Output.ContainsKey(PreviousCaptureName))) return json;
        var root = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        var outputs = root["Devices"]!["Output"]!.AsObject();
        // Preserve property order as well as legacy names: resume keys previously serialized this exact configuration.
        var entries = outputs.ToArray(); outputs.Clear();
        foreach (var entry in entries) outputs.Add(entry.Key is CaptureName or PreviousCaptureName ? LegacyCaptureName : entry.Key, entry.Value?.DeepClone());
        foreach (var item in root["Tests"]!.AsArray())
            if (item?["Recording In"]?.GetValue<string>() is CaptureName or PreviousCaptureName) item["Recording In"] = LegacyCaptureName;
        if (root["Description"]?.GetValue<string>() is string description) root["Description"] = description.Replace(CaptureName, LegacyCaptureName, StringComparison.Ordinal).Replace(PreviousCaptureName, LegacyCaptureName, StringComparison.Ordinal);
        return root.ToJsonString();
    }
    public static bool Matches(InOutConfig? config) => config?.AutomationProfile is Profile or Acoustic3ModelDefinition.Profile;
    public const string AcousticRubBuzzStage = "AcousticRubBuzz";
    public static C360ElectronicStage Stage(TestConfig test) => test.SpeakerStage switch
    {
        "MicDry" => C360ElectronicStage.MicDry, "Bluetooth" => C360ElectronicStage.Bluetooth, "MicEffects" => C360ElectronicStage.MicEffects, "MicFrequencyShift" => C360ElectronicStage.MicFrequencyShift, "LineIn" => C360ElectronicStage.LineIn, "UsbA" => C360ElectronicStage.UsbA, "AcousticMic" => C360ElectronicStage.AcousticMic,
        AcousticRubBuzzStage => C360ElectronicStage.AcousticRubBuzz, "MicFeedbackGain" => C360ElectronicStage.MicFeedbackGain,
        _ => throw new ArgumentException($"{test.id}: chọn ngõ loa MIC IN / LINE IN / USB A trong cấu hình model.")
    };
    /// <summary>Stages captured by the external measurement mic (played through LINE IN), not the electronic recording route.</summary>
    public static bool IsAcoustic(C360ElectronicStage stage) => stage is C360ElectronicStage.AcousticMic or C360ElectronicStage.AcousticRubBuzz;
    /// <summary>Stages whose capture is the external measurement mic (acoustic tests and the IO009 howl loop).</summary>
    public static bool RecordsFromMeasurementMic(C360ElectronicStage stage) => IsAcoustic(stage) || stage == C360ElectronicStage.MicFeedbackGain;
    public static bool IsAcoustic(TestConfig test) => test.SpeakerStage is "AcousticMic" or AcousticRubBuzzStage;
    /// <summary>Stages played from the analog sound card (FastTrack A/B), as opposed to USB A or Bluetooth.</summary>
    public static bool UsesAnalogPlayback(C360ElectronicStage stage) => stage is not (C360ElectronicStage.UsbA or C360ElectronicStage.Bluetooth);
    public static void Validate(InOutConfig config)
    {
        config.TestVolume?.Validate();
        var tests = config.Tests.Where(t => t.IsEnabled).ToArray();
        if (tests.Length == 0) throw new ArgumentException("Chưa bật bài đo nào cho model.");
        if (tests.Any(t => string.IsNullOrWhiteSpace(t.id) || string.IsNullOrWhiteSpace(t.name))
            || tests.Select(t => t.id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != tests.Length)
            throw new ArgumentException("Các bài đo phải có mã riêng và tên đầy đủ.");
        if (tests.Select(Stage).Distinct().Count() != tests.Length) throw new ArgumentException("Mỗi ngõ C360 chỉ có một bài đang bật.");
        foreach (var t in tests)
        {
            var stage = Stage(t); t.ImpulseEffects?.Validate(); t.TestVolume?.Validate();
            _ = t.MicrophoneCalibration?.Resolve();
            if (IsAcoustic(stage) && t.AcousticSetupPending)
                throw new ArgumentException($"{t.id}: cấu hình mic đang lưu tạm; chọn mic thật, đo chỉnh và xác nhận hoàn tất trước khi bật Auto Test.");
            if (stage == C360ElectronicStage.AcousticRubBuzz && !(t.IsRubBuzzEnabled && t.UsesAcousticRubBuzz))
                throw new ArgumentException($"{t.id}: bài Mic thực tế · Rè cần bật Rè Pink/Chirp (TestRubBuzz + RubBuzzMode PinkNoise/Chirp/PinkNoiseChirp).");
            if (!config.AcousticCapture && !IsAcoustic(stage) && t.MicrophoneCalibration != null)
                throw new ArgumentException($"{t.id}: calibration mic chỉ dùng cho bài mic thực tế, không áp vào đường điện tử {CaptureNameFor(config)}.");
            t.FrequencyShift?.Validate();
            t.ValidateWindowsLevels();
            t.ValidateMeasurementCriteria();
            t.ValidateDipWindows();
            t.ValidateRunPolicy();
            if (stage == C360ElectronicStage.MicEffects && !t.TestEffect || stage != C360ElectronicStage.MicEffects && t.TestEffect)
                throw new ArgumentException($"{t.id}: bật Effect cho bài MIC IN Echo/Reverb; các ngõ còn lại dùng bài riêng tương ứng.");
            bool sweep = string.Equals(t.FrequencyResponseMethod, "LogSweep", StringComparison.OrdinalIgnoreCase) || string.Equals(t.FrequencyResponseMethod, "REW", StringComparison.OrdinalIgnoreCase);
            t.FeedbackGain?.Validate();
            if (stage != C360ElectronicStage.MicFeedbackGain && (stage == C360ElectronicStage.MicFrequencyShift ? !string.Equals(t.FrequencyResponseMethod, "Multitone", StringComparison.OrdinalIgnoreCase) : !sweep))
                throw new ArgumentException($"{t.id}: chọn {(stage == C360ElectronicStage.MicFrequencyShift ? "Multitone cho di tần MIC" : "LogSweep cho effect/đáp tuyến/THD C360")}.");
            if (t.LogSweepRuns is < 1 or > 3 || t.MinimumInputSignalDbFs is double minimum && (!double.IsFinite(minimum) || minimum < -100 || minimum > -6))
                throw new ArgumentException($"{t.id}: số lượt 1–3, ngưỡng tín hiệu thu từ -100 đến -6 dBFS.");
            if (t.TestAirLeak || t.TestLooseParticle || t.TestSoundLevel || AdditionalTestRegistry.HasAnyEnabled(t)
                && !(IsAcoustic(stage) && t.UsesAcousticRubBuzz))
                throw new ArgumentException($"{t.id}: Rè pink/chirp chỉ dùng ở bài mic âm học; không đo cơ khí từ {CaptureNameFor(config)} điện tử.");
            t.ValidateAdditionalTests();
            if (!config.Devices.Input.ContainsKey(t.PlaybackOut) || !config.Devices.Output.ContainsKey(t.RecordingIn)) throw new ArgumentException($"{t.id}: thiếu cấu hình ngõ phát/thu.");
        }
        var electronic = tests.Where(t => !RecordsFromMeasurementMic(Stage(t))).ToArray();
        if (electronic.Select(t => config.Devices.Output[t.RecordingIn]).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1
            || electronic.Select(t => t.RecordingChannel).Distinct().Count() > 1)
            throw new ArgumentException($"Chuỗi C360 phải dùng cùng một ngõ thu {CaptureNameFor(config)} và cùng kênh thu.");
        foreach (var acoustic in tests.Where(t => RecordsFromMeasurementMic(Stage(t))))
            if (electronic.Any(t => config.Devices.Output[t.RecordingIn] == config.Devices.Output[acoustic.RecordingIn]))
                throw new ArgumentException($"{acoustic.id}: chọn mic đo ngoài, khác đường thu {CaptureNameFor(config)} điện tử.");
    }
    public static bool EnsureSeed(CheckingConfig config)
    {
        var electronic = config.models.Where(m => m.testItems?.InOut?.AutomationProfile == Profile).ToArray();
        if (electronic.Length > 0)
        {
            bool changed = false;
            foreach (var model in electronic)
            {
                var io = model.testItems.InOut;
                if (io.AutomationProfileVersion < 7 && io.TestVolume?.IsEmpty != false)
                {
                    io.TestVolume=ModelTestVolume.C360Defaults(); changed=true;
                    model.AudioConfigUpdatedUtc=DateTimeOffset.UtcNow;
                }
                if (io.AutomationProfileVersion < 5)
                {
                    AddAcousticDraft(io); changed = true;
                    model.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow;
                }
                if (io.AutomationProfileVersion >= 4)
                {
                    if (io.AutomationProfileVersion < 6) AddMusicAndDryTests(io);
                    if (io.AutomationProfileVersion < 7) {io.AutomationProfileVersion=7;model.AudioConfigUpdatedUtc=DateTimeOffset.UtcNow;changed=true;}
                    continue;
                }
                if (io.AutomationProfileVersion >= 3)
                {
                    if (io.Devices.Input.TryGetValue("USB A", out string? prior) && prior == "Speakers (C360 usb audio)")
                        io.Devices.Input["USB A"] = "CS3600 MUSIC";
                    AddMusicAndDryTests(io); io.AutomationProfileVersion = 7; model.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow; changed = true;
                    continue;
                }
                var mic = io.Tests.FirstOrDefault(t => t.SpeakerStage == "MicEffects");
                if (io.AutomationProfileVersion < 2 && mic != null && !io.Tests.Any(t => t.SpeakerStage == "MicFrequencyShift"))
                {
                    string id = "IO004"; int number = 5;
                    while (io.Tests.Any(t => t.id == id)) id = $"IO{number++:000}";
                    var test = ShiftTest(id, mic.PlaybackOut, mic.RecordingIn);
                    test.PlaybackChannel = mic.PlaybackChannel; test.RecordingChannel = mic.RecordingChannel;
                    test.PlaybackLevelDbfs = Math.Min(mic.PlaybackLevelDbfs ?? -18, -18);
                    io.Tests.Insert(io.Tests.IndexOf(mic) + 1, test);
                }
                var canonical = new Dictionary<string, string> { ["MicEffects"] = "IO001", ["MicFrequencyShift"] = "IO002", ["LineIn"] = "IO003", ["UsbA"] = "IO004" };
                foreach (var test in io.Tests) if (test.SpeakerStage != null && canonical.TryGetValue(test.SpeakerStage, out string? id)) test.id = id;
                var used = new HashSet<string>(canonical.Values); int next = 5;
                foreach (var test in io.Tests.Where(t => t.SpeakerStage == null || !canonical.ContainsKey(t.SpeakerStage)))
                { while (used.Contains($"IO{next:000}")) next++; if (!used.Add(test.id)) { test.id = $"IO{next++:000}"; used.Add(test.id); } }
                AddMusicAndDryTests(io); io.AutomationProfileVersion = 7; model.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow; changed = true;
            }
            foreach (var model in electronic.Where(m => m.testItems.InOut.AutomationProfileVersion < 8))
            {
                var io = model.testItems.InOut;
                for (int i = 0; i < io.Tests.Count; i++)
                {
                    var test = io.Tests[i]; string id = $"IO{i + 1:000}";
                    if (test.id != id) { test.ReferenceTestId ??= test.id; test.id = id; }
                }
                io.AutomationProfileVersion = 8; model.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow; changed = true;
            }
            foreach (var model in electronic.Where(m => m.testItems.InOut.AutomationProfileVersion < 9))
            {
                foreach (var test in model.testItems.InOut.Tests.Where(t => t.SweepLengthSamples == null
                    && t.LogSweepDurationSeconds is double duration && Math.Abs(duration - 262144d / 44100) < 1e-9))
                { test.SweepLengthSamples = SweepLengthSettings.DefaultSamples; test.LogSweepDurationSeconds = null; }
                model.testItems.InOut.AutomationProfileVersion = 9; model.AudioConfigUpdatedUtc = DateTimeOffset.UtcNow; changed = true;
            }
            return changed;
        }
        string name = "C360"; int suffix = 2;
        while (config.models.Any(m => string.Equals(m.model, name, StringComparison.OrdinalIgnoreCase))) name = "C360 " + suffix++;
        config.models.Add(Create(name)); return true;
    }
    public static ModelConfig Create(string name = "C360") => new()
    {
        model = name, itemCount = 1, AudioConfigUpdatedUtc = DateTimeOffset.UtcNow,
        testItems = new() { InOut = new()
        {
            AutomationProfile = Profile, AutomationProfileVersion = 9, TestVolume=ModelTestVolume.C360Defaults(),
            Description = "Điện tử: MIC IN echo/reverb → MIC IN di tần (FBX tắt/bật) → MIC IN không effect → tắt MIC/ECHO/REVERB → LINE IN → USB A → BT. Thu cố định RECORDING TYPE C (C360 USB-C recording).",
            Devices = new() { Input = new() { ["MIC IN"] = "Speakers (6- USB Audio Device)", ["LINE IN"] = "Speakers (USB Audio Device)", ["USB A"] = "CS3600 MUSIC", ["BT"] = "KBEATBOX" },
                Output = new() { [CaptureName] = "Headset Microphone (C360 usb audio)", ["Measurement Mic"] = "KT USB" } },
            Tests = new()
            {
                Test("IO001", "MIC IN · Echo / Reverb", "MIC IN", "MicEffects", true),
                ShiftTest("IO002", "MIC IN", CaptureName),
                Test("IO003", "MIC IN · Không effect", "MIC IN", "MicDry", false),
                Test("IO004", "LINE IN · Đáp tuyến / THD", "LINE IN", "LineIn", false),
                Test("IO005", "USB A · Đáp tuyến / THD", "USB A", "UsbA", false),
                AcousticDraft("IO006"),
                Test("IO007", "BT · Đáp tuyến / THD", "BT", "Bluetooth", false)
            }
        } }
    };
    private static void AddMusicAndDryTests(InOutConfig io)
    {
        string FreeId(int start) { while (io.Tests.Any(t => string.Equals(t.id, $"IO{start:000}", StringComparison.OrdinalIgnoreCase))) start++; return $"IO{start:000}"; }
        var mic = io.Tests.FirstOrDefault(t => t.SpeakerStage == "MicEffects") ?? io.Tests.FirstOrDefault(t => t.SpeakerStage == "MicFrequencyShift");
        if (mic != null && !io.Tests.Any(t => t.SpeakerStage == "MicDry"))
        {
            var dry = Test(FreeId(6), "MIC IN · Không effect", mic.PlaybackOut, "MicDry", false);
            dry.RecordingIn = mic.RecordingIn; dry.PlaybackChannel = mic.PlaybackChannel; dry.RecordingChannel = mic.RecordingChannel;
            dry.PlaybackLevelDbfs = mic.PlaybackLevelDbfs;
            dry.WindowsPlaybackPercent = mic.WindowsPlaybackPercent; dry.WindowsRecordingPercent = mic.WindowsRecordingPercent;
            dry.LogSweepDurationSeconds = mic.LogSweepDurationSeconds;
            dry.SweepLengthSamples = mic.SweepLengthSamples;
            dry.MinimumInputSignalDbFs = mic.MinimumInputSignalDbFs; dry.MinimumSnrDb = mic.MinimumSnrDb;
            int lastMic = io.Tests.FindLastIndex(t => t.SpeakerStage is "MicEffects" or "MicFrequencyShift");
            io.Tests.Insert(lastMic + 1, dry);
        }
        if (!io.Tests.Any(t => t.SpeakerStage == "Bluetooth"))
        {
            string key = "BT"; int n = 2;
            while (io.Devices.Input.ContainsKey(key)) key = "BT " + n++;
            io.Devices.Input[key] = "KBEATBOX";
            var bt = Test(FreeId(7), "BT · Đáp tuyến / THD", key, "Bluetooth", false);
            var capture = io.Tests.FirstOrDefault(t => t.SpeakerStage != "AcousticMic");
            if (capture != null) { bt.RecordingIn = capture.RecordingIn; bt.RecordingChannel = capture.RecordingChannel; }
            io.Tests.Add(bt);
        }
    }
    private static TestConfig AcousticDraft(string id) => new()
    {
        id = id, name = "Mic thực tế · Đáp tuyến / THD (cấu hình tạm)", SpeakerStage = "AcousticMic",
        PlaybackOut = "LINE IN", RecordingIn = "Measurement Mic", Enable = false, AcousticSetupPending = true,
        FrequencyResponseMethod = "LogSweep", SweepLengthSamples = SweepLengthSettings.DefaultSamples, PlaybackLevelDbfs = -24,
        MinimumInputSignalDbFs = -65, MinimumSnrDb = 20, ThdLimit = .5, BassThdLimit = 1,
        BassThdFrequency = 80, MidThdFrequency = 1000, TrebleThdFrequency = 4000
    };
    private static void AddAcousticDraft(InOutConfig io)
    {
        if (io.Tests.Any(t => t.SpeakerStage == "AcousticMic")) return;
        string id = "IO005"; int next = 6; while (io.Tests.Any(t => t.id == id)) id = $"IO{next++:000}";
        var test = AcousticDraft(id);
        test.PlaybackOut = io.Tests.FirstOrDefault(t => t.SpeakerStage == "LineIn")?.PlaybackOut ?? io.Devices.Input.Keys.First();
        string key = "Measurement Mic"; int suffix = 2;
        while (io.Devices.Output.ContainsKey(key)) key = "Measurement Mic " + suffix++;
        io.Devices.Output[key] = "KT USB"; test.RecordingIn = key;
        io.Tests.Add(test);
    }
    private static TestConfig ShiftTest(string id, string output, string input) => new()
    {
        id = id, name = "MIC IN · Di tần (FBX)", SpeakerStage = "MicFrequencyShift", PlaybackOut = output, RecordingIn = input,
        Enable = true, PlaybackLevelDbfs = -18, FrequencyResponseMethod = "Multitone", FrequencyShift = new()
    };
    private static TestConfig Test(string id, string name, string output, string stage, bool effects) => new()
    {
        id = id, name = name, SpeakerStage = stage, PlaybackOut = output, RecordingIn = CaptureName, Enable = true,
        FrequencyResponseMethod = "LogSweep", SweepLengthSamples = SweepLengthSettings.DefaultSamples, PlaybackLevelDbfs = -12,
        ThdLimit = .5, BassThdLimit = 1.0, BassThdFrequency = 80, MidThdFrequency = 1000, TrebleThdFrequency = 4000,
        MinimumInputSignalDbFs = -75, LogSweepRuns = 1, TestEffect = effects,
        ImpulseEffects = effects ? new() { ExpectEcho = true, ExpectReverb = true } : null
    };
}

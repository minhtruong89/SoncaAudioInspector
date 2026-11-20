using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace SoncaAudioInspector;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SerialSource { Scanned, Manual, Device, Auto }
public sealed class ProductMeasurementContext
{
    public string Serial { get; private set; } = "";
    public string Model { get; private set; } = "";
    public SerialSource Source { get; private set; } = SerialSource.Auto;
    public string QaVerdict { get; private set; } = "NONE";
    public DateTimeOffset? QaConfirmedAt { get; private set; }
    public string? QaConfirmedBy { get; private set; }
    public string? OperatorNote { get; private set; }
    public bool RequiresQaConfirmation => Source is SerialSource.Manual or SerialSource.Scanned && Serial.Length > 0 && QaConfirmedAt == null;
    public static string? NormalizeOperatorNote(string? note)
    {
        if (string.IsNullOrWhiteSpace(note)) return null;
        string trimmed = note.Trim();
        return trimmed.Length <= 4000 ? trimmed : trimmed[..4000];
    }
    public static string ValidateSerial(string value)
    {
        string serial = value.Trim();
        if (serial.Length is 0 or > 64 || serial.Any(c => char.IsControl(c) || char.IsSurrogate(c)
            || char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.OtherNotAssigned or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new ArgumentException("Serial cần 1–64 ký tự in được.");
        return serial;
    }
    public static string FileToken(string serial)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string token = new(serial.Select(c => invalid.Contains(c) || c == ' ' ? '_' : c).ToArray());
        return token == serial && !token.EndsWith('.') ? token
            : token.TrimEnd('.') + "-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serial)))[..8];
    }
    public void Receive(string value, string model, SerialSource source)
    {
        string serial = ValidateSerial(value);
        if (Serial == serial && Model == model && Source == source) return;
        Serial = serial; Model = model; Source = source; QaVerdict = "NONE"; QaConfirmedAt = null; QaConfirmedBy = null; OperatorNote = null;
    }
    public void ConfirmQa(string verdict, string who, string? operatorNote = null, string? model = null)
    {
        if (verdict is not ("PASS" or "FAIL" or "NONE")) throw new ArgumentException("QA không hợp lệ.");
        if (!string.IsNullOrWhiteSpace(model)) Model = model.Trim();
        QaVerdict = verdict; QaConfirmedAt = DateTimeOffset.Now; QaConfirmedBy = who; OperatorNote = NormalizeOperatorNote(operatorNote);
    }
    public string Resolve(string model, string? deviceSerial, Func<string> generate)
    {
        if (Serial.Length > 0 && Model == model && Source is SerialSource.Scanned or SerialSource.Manual) return Serial;
        var (verdict, confirmedAt, confirmedBy, note) = Serial.Length == 0 || Model == model
            ? (QaVerdict, QaConfirmedAt, QaConfirmedBy, OperatorNote) : ("NONE", (DateTimeOffset?)null, (string?)null, (string?)null);
        if (!string.IsNullOrWhiteSpace(deviceSerial))
        {
            Receive(deviceSerial, model, SerialSource.Device);
            RestoreConfirmedQa(verdict, confirmedAt, confirmedBy, note);
            return Serial;
        }
        if (Serial.Length > 0 && Model == model && Source == SerialSource.Auto) return Serial;
        Receive(generate(), model, SerialSource.Auto);
        RestoreConfirmedQa(verdict, confirmedAt, confirmedBy, note);
        return Serial;
    }
    private void RestoreConfirmedQa(string verdict, DateTimeOffset? confirmedAt, string? confirmedBy, string? note)
    {
        if (confirmedAt == null) return;
        QaVerdict = verdict; QaConfirmedAt = confirmedAt; QaConfirmedBy = confirmedBy; OperatorNote = note;
    }
    public void Clear() { Serial = Model = ""; Source = SerialSource.Auto; QaVerdict = "NONE"; QaConfirmedAt = null; QaConfirmedBy = null; OperatorNote = null; }
    public MeasurementIdentity Snapshot()
    {
        var sessions = SessionModelConfigurations.Current;
        if (sessions.Find(Model) is { } trial)
            return new(Serial, Model, Source, QaVerdict, QaConfirmedAt, QaConfirmedBy, sessions.ServerTrialRevision(Model), null,
                true, System.Text.Json.JsonSerializer.Serialize(trial, ModelConfigEditorWindow.JsonOptions), OperatorNote);
        var revision = ActiveAudioConfigSync.Current.FindRevision(Model);
        return new(Serial, Model, Source, QaVerdict, QaConfirmedAt, QaConfirmedBy, revision?.RevisionId, revision?.Serial, false, null, OperatorNote);
    }
}
public sealed record MeasurementIdentity(string Serial, string Model, SerialSource Source, string QaVerdict, DateTimeOffset? QaConfirmedAt, string? QaConfirmedBy, string? ConfigRevisionId = null, long? ConfigSerial = null,
    bool IsTrial = false, string? TrialConfigJson = null, string? OperatorNote = null);

public sealed record QaComparisonResult(string Text, string Color);
public static class QaComparison
{
    public static QaComparisonResult Compare(string appVerdict, string qaVerdict) => qaVerdict is not ("PASS" or "FAIL")
        ? new("CHƯA CÓ QA", "#A1A1AA") : appVerdict == qaVerdict ? new("KHỚP", "#10B981")
        : new($"LỆCH · App {appVerdict} / QA {qaVerdict}", "#F59E0B");
}

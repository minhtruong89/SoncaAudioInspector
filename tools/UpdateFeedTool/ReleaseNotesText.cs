using System.Text;

// Release notes must be real UTF-8 text. Windows PowerShell 5.1 reads a BOM-less UTF-8 file with the ANSI code page,
// which turns "Duyệt" into "DuyÃ¡Â»‡t"; the 1.4.0 summary was signed like that. The builder refuses such text, and the
// history page repairs already-signed archives when it renders them (signed manifest bytes never change).
internal static class ReleaseNotesText
{
    private static readonly Encoding Ansi;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(false, throwOnInvalidBytes: true);

    static ReleaseNotesText()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Ansi = Encoding.GetEncoding(1252, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
    }

    // Returns the UTF-8 text that had been mis-decoded as Windows-1252, or the input unchanged when it is not mojibake.
    // Genuine Vietnamese text always contains letters outside Windows-1252 (ệ, ấ, ơ…) or bytes that are not valid UTF-8,
    // so it is never "repaired".
    public static string Repair(string text)
    {
        if (string.IsNullOrEmpty(text) || text.All(c => c < 0x80)) return text;
        try
        {
            string decoded = StrictUtf8.GetString(Ansi.GetBytes(text));
            return decoded.Length > 0 && decoded != text ? decoded : text;
        }
        catch (Exception ex) when (ex is EncoderFallbackException or DecoderFallbackException or ArgumentException)
        {
            return text;
        }
    }

    public static bool IsMisdecoded(string text) => Repair(text) != text;

    public static string RequireUtf8(string text, string what) => IsMisdecoded(text)
        ? throw new InvalidDataException(what + " bị lỗi mã hóa (UTF-8 bị đọc thành ANSI, ví dụ \"Duyá»‡t\" thay vì \"Duyệt\"). "
            + "Đọc file ghi chú bằng UTF-8, ví dụ Get-Content -Encoding UTF8.")
        : text;
}

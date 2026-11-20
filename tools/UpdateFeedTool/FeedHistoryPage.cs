using System.Globalization;
using System.Net;
using System.Text;
using SoncaAudioInspector;
using SoncaAudioInspector.Update;

internal static class FeedHistoryPage
{
    internal static IEnumerable<(string Path, UpdateManifest Manifest)> Versions(string feed, string publicKey)
    {
        string directory = Path.Combine(feed, "versions"); UpdatePaths.RejectLinks(directory);
        foreach (string path in Directory.EnumerateFiles(directory, "*.json", SearchOption.TopDirectoryOnly))
        {
            UpdatePaths.RejectLinks(path); UpdatePaths.RejectLinks(path + ".sig");
            byte[] bytes = File.ReadAllBytes(path); UpdateSignature.Verify(bytes, File.ReadAllText(path + ".sig"), publicKey);
            var manifest = UpdateManifest.Read(bytes); manifest.Validate();
            if (Path.GetFileNameWithoutExtension(path) != manifest.Version) throw new InvalidDataException("Tên archive khác phiên bản.");
            yield return (path, manifest);
        }
    }
    public static void CopyHistory(string previous, string output, string key)
    {
        foreach (var (source, manifest) in Versions(previous, key))
        {
            foreach (var file in manifest.Files)
            {
                string blob = Path.Combine(previous, "files", file.Sha256); UpdatePaths.RejectLinks(blob);
                if (!UpdateSignature.Matches(blob, file)) throw new InvalidDataException("Previous feed thiếu/sai blob.");
                CopyImmutable(blob, Path.Combine(output, "files", file.Sha256));
            }
            CopyImmutable(Package(previous, manifest.Version), Package(output, manifest.Version));
            CopyImmutable(source, Path.Combine(output, "versions", Path.GetFileName(source)));
            CopyImmutable(source + ".sig", Path.Combine(output, "versions", Path.GetFileName(source) + ".sig"));
        }
    }
    private static string Package(string feed, string version) => Path.Combine(feed, "packages", version, "SoncaAudioInspector-win-x64.zip");
    private static void CopyImmutable(string source, string target)
    {
        UpdatePaths.RejectLinks(source); UpdatePaths.RejectLinks(target);
        if (File.Exists(target))
        {
            if (UpdateSignature.Hash(source) != UpdateSignature.Hash(target)) throw new IOException("Không ghi đè nội dung bất biến: " + target);
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!); File.Copy(source, target);
    }
    public static void Write(string feed, string latestVersion, string key, string? outputPath = null)
    {
        string E(string value) => WebUtility.HtmlEncode(value);
        var html = new StringBuilder("""
<!doctype html>
<html lang="vi"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<base href="/updates/audio-inspector/"><link rel="canonical" href="https://inventory.acnos.store/update">
<title>SONCA Audio Inspector — Phiên bản</title><style>
.site-paths{display:block;margin-bottom:8px}.site-paths a{color:#6ee7b7}
.badge.beta{background:#3b2f0b;color:#fcd34d;border-color:#b45309}.beta-note{color:#fcd34d}
.features{margin-top:12px}.features summary{cursor:pointer;color:#a1a1aa}.features summary:hover{color:#e4e4e7}.features ul{margin-top:8px}
*{box-sizing:border-box}body{margin:0;background:#101114;color:#e4e4e7;font:16px/1.65 system-ui,sans-serif}main{max-width:860px;margin:auto;padding:28px 18px}h1{font-size:clamp(1.6rem,5vw,2.4rem)}article{margin:22px 0;padding:24px;border:1px solid #34343b;border-radius:14px;background:#1b1d22}.latest{border:2px solid #34d399}h2{margin:0;color:#6ee7b7}.badge{display:inline-block;background:#064e3b;color:#a7f3d0;padding:3px 12px;border-radius:20px}a{color:#7dd3fc;overflow-wrap:anywhere}.hash{font:12px/1.7 monospace;overflow-wrap:anywhere}.notes{white-space:pre-wrap}time,.muted{color:#a1a1aa}ul{padding-left:22px}footer{padding:20px 0}@media(max-width:480px){article{padding:18px}}
.update-guide{border:2px solid #34d399;border-radius:14px;padding:24px;background:#112b24;margin:24px 0 32px}.update-guide h2{margin-top:0}.update-guide ol{padding-left:24px}.update-guide li{margin:10px 0}kbd{display:inline-block;background:#1b1d22;border:1px solid #52655e;border-radius:5px;padding:1px 7px;font:inherit;font-size:.9em}.portable{margin-top:20px;border-top:1px solid #34343b;padding-top:16px}.portable summary{cursor:pointer;color:#a1a1aa}.portable summary:hover{color:#e4e4e7}.portable a{display:inline-block;padding:8px 0}.section-title{color:#e4e4e7;font-size:1.3rem}.hash{display:block;margin-top:10px}@media(max-width:480px){.update-guide{padding:18px}}
</style></head><body><main><h1>SONCA Audio Inspector — Cập nhật</h1>
<section class="update-guide" aria-labelledby="update-heading">
<h2 id="update-heading">Đã cài app? Cập nhật ngay trong ứng dụng</h2>
<p>Dùng nút <strong>Kiểm tra cập nhật</strong> trong Sonca Audio Inspector. Ứng dụng chỉ tải các file thay đổi, kiểm tra chữ ký và tự thay file chương trình khi sẵn sàng.</p>
<ol><li>Mở Sonca Audio Inspector và chờ phép đo hoặc thao tác sửa model kết thúc.</li>
<li>Nhấn <kbd>Ctrl</kbd> + <kbd>Shift</kbd> + <kbd>I</kbd>, chọn tab <strong>Phiên bản</strong>.</li>
<li>Bấm <strong>Kiểm tra cập nhật</strong> và làm theo hướng dẫn trong app.</li></ol>
<p class="muted">Cấu hình, hiệu chuẩn và dữ liệu chuẩn của trạm được giữ nguyên. Nếu app của bạn chưa có bộ cập nhật hoặc không mở được, xem gói portable bên dưới.</p>
</section>
<h2 class="section-title">Lịch sử phiên bản &amp; gói portable</h2>
<p class="muted">ZIP dành cho cài mới hoặc khôi phục thủ công. Khi đã có bộ cập nhật trong app, hãy dùng hướng dẫn phía trên.</p>
""");
        foreach (var (_, m) in Versions(feed, key).OrderByDescending(v => UpdateManifest.ParseVersion(v.Manifest.Version)))
        {
            string package = Package(feed, m.Version); UpdatePaths.RejectLinks(package);
            long size = new FileInfo(package).Length;
            string packageHash = UpdateSignature.Hash(package);
            bool latest = m.Version == latestVersion;
            html.Append("<article class=\"").Append(latest ? "latest" : m.IsBeta ? "release beta" : "release").Append("\" data-version=\"").Append(E(m.Version))
                .Append(m.IsBeta ? "\" data-channel=\"beta" : "").Append("\"><h2>")
                .Append(E(AppVersion.Format(UpdateManifest.ParseVersion(m.Version)))).Append(m.IsBeta ? " beta" : "").Append("</h2>");
            if (latest) html.Append("<p class=\"badge\">Phiên bản mới nhất</p>");
            if (m.IsBeta) html.Append("<p class=\"badge beta\">BETA · chưa kiểm tra kỹ</p><p class=\"beta-note\">Không tự cập nhật cho các máy: chỉ máy chọn ")
                .Append("<strong>Phiên bản → Kênh cập nhật → Beta</strong> mới được mời cài. Nếu không ổn, vào <strong>Cài phiên bản khác / Hạ cấp</strong> để về bản ổn định.</p>");
            html.Append("<time>").Append(E(m.ReleasedUtc.UtcDateTime.ToString("dd/MM/yyyy HH:mm 'UTC'", CultureInfo.InvariantCulture))).Append("</time>");
            // Signed archives are immutable, so text that an old release signed mis-decoded is repaired only for display.
            // Since 1.4 the summary line is also the first feature: show it once, as the summary.
            string notes = ReleaseNotesText.Repair(m.Notes);
            string[] features = (m.Features ?? []).Select(ReleaseNotesText.Repair).ToArray();
            bool summaryIsFirstFeature = features.Length > 0 && UpdateManifest.SingleLine(notes) == UpdateManifest.SingleLine(features[0]);
            html.Append("<p class=\"notes\">").Append(E(notes)).Append("</p>");
            // Only the summary is shown; the feature list opens on click, like the portable package section.
            string[] details = (summaryIsFirstFeature ? features.Skip(1) : features).ToArray();
            if (details.Length > 0)
            {
                html.Append("<details class=\"features\"><summary>Chi tiết tính năng (").Append(details.Length).Append(")</summary><ul>");
                foreach (string feature in details) html.Append("<li>").Append(E(feature)).Append("</li>");
                html.Append("</ul></details>");
            }
            html.Append("<details class=\"portable\"><summary>Gói portable · Cài mới / khôi phục</summary><p>Giải nén vào thư mục mới khi cài mới. Khi khôi phục, sao lưu ứng dụng và dữ liệu trạm trước khi thay file chương trình; giữ cấu hình, hiệu chuẩn và các file chuẩn hiện có.</p><p><a href=\"").Append(E("packages/" + m.Version + "/SoncaAudioInspector-win-x64.zip?sha256=" + packageHash))
                .Append("\">Tải ZIP portable win-x64</a> · ").Append((size / 1048576d).ToString("0.00", CultureInfo.InvariantCulture))
                .Append(" MB (").Append(size.ToString(CultureInfo.InvariantCulture)).Append(" byte)</p><p class=\"hash\">SHA-256: ")
                .Append(E(packageHash)).Append("</p></details></article>\n");
        }
        // The history page stays JavaScript-free (release notes are rendered here), so the site's canonical paths are listed
        // as plain links; the inventory web app offers the same list behind Ctrl + /.
        html.Append("<footer class=\"muted\"><nav class=\"site-paths\" aria-label=\"Đường dẫn inventory.acnos.store\">Đường dẫn: ")
            .Append("<a href=\"/\">/</a> Bảng điều khiển · <a href=\"/report\">/report</a> Báo cáo App vs QA · ")
            .Append("<a href=\"/update\">/update</a> Lịch sử phiên bản · <a href=\"/temp-updates\">/temp-updates</a> Bản tạm (kênh Beta, ADMIN duyệt)</nav>SONCA Audio Inspector · Lịch sử phát hành</footer></main></body></html>\n");
        string target = outputPath == null ? Path.Combine(feed, "index.html") : Path.GetFullPath(outputPath);
        UpdatePaths.RejectLinks(target);
        File.WriteAllText(target, html.ToString(), new UTF8Encoding(false));
    }
}

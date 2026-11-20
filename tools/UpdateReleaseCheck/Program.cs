using System.Security.Cryptography;
using System.Text.Json;
using SoncaAudioInspector.Update;

// End-to-end release check against the public production feed, applying only to
// a fresh synthetic station under the requested artifact directory.
// Usage: <previous-feed> <new-feed> <new-output-dir> [beta]
//   stable (default): previous latest -> new latest through the stable channel.
//   beta: a stable-channel station is NOT offered the beta; a beta-channel station installs it; the signed catalog
//         lists both; and the beta station can downgrade back to the previous stable release.
string previous = Path.GetFullPath(args[0]), expectedFeed = Path.GetFullPath(args[1]), output = Path.GetFullPath(args[2]);
bool betaMode = args.Length > 3 && args[3] == "beta";
if (Directory.Exists(output)) throw new IOException("Verification output must be new.");
Directory.CreateDirectory(output);
UpdateManifest ReadSigned(string feed, string pointer = "latest.json")
{
    byte[] bytes = File.ReadAllBytes(Path.Combine(feed, pointer));
    UpdateSignature.Verify(bytes, File.ReadAllText(Path.Combine(feed, pointer + ".sig")));
    var manifest = UpdateManifest.Read(bytes); manifest.Validate(); return manifest;
}
bool SameManifest(UpdateManifest a, UpdateManifest b) => JsonSerializer.SerializeToUtf8Bytes(a, UpdateManifest.JsonOptions)
    .SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(b, UpdateManifest.JsonOptions));
var old = ReadSigned(previous); var expected = ReadSigned(expectedFeed, betaMode ? "beta.json" : "latest.json");
if (betaMode && (!expected.IsBeta || old.IsBeta)) throw new InvalidDataException("Beta mode needs a stable previous feed and a beta.json marked beta.");
string station = Path.Combine(output, "synthetic-station"); Directory.CreateDirectory(station);
foreach (var file in old.Files)
{
    string path = UpdatePaths.Resolve(station, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    string blob = Path.Combine(previous, "files", file.Sha256);
    if (!UpdateSignature.Matches(blob, file)) throw new InvalidDataException("Old snapshot corrupt: " + file.Path);
    File.Copy(blob, path);
}
var protectedPaths = new[] { "checking_config.json", "calibrations/synthetic.txt", "save standards/synthetic.csv", "unknown-station.dat" };
foreach (string relative in protectedPaths)
{
    string path = Path.Combine(station, relative); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "synthetic-station-data:" + relative);
}
void AssertStationData()
{
    foreach (string relative in protectedPaths)
        if (File.ReadAllText(Path.Combine(station, relative)) != "synthetic-station-data:" + relative) throw new Exception("Station data changed.");
}
using var client = new UpdateClient();
if (betaMode)
{
    var stableOffer = await client.CheckAsync(new Version(old.Version), UpdateChannelKind.Stable);
    if (stableOffer != null) throw new Exception($"Stable-channel station was offered {stableOffer.Version}; a beta must not change latest.json.");
    Console.WriteLine($"PASS stable-channel station on {old.Version} is not offered any update (live latest.json unchanged).");
}
var channel = betaMode ? UpdateChannelKind.Beta : UpdateChannelKind.Stable;
var live = await client.CheckAsync(new Version(old.Version), channel) ?? throw new Exception("No newer release visible.");
if (live.Version != expected.Version || !SameManifest(live, expected))
    throw new Exception("Public signed manifest differs from intended release.");
var plan = UpdateClient.CalculateChanges(live, station);
Console.WriteLine($"PASS live signature/manifest ({channel}): {old.Version} -> {live.Version}; delta {plan.Files.Count} files / {plan.TotalBytes} bytes");
await client.DownloadAsync(plan, station);
UpdateInstaller.Apply(plan, station, new Version(old.Version), () => false);
if (!live.Files.All(f => UpdateSignature.Matches(UpdatePaths.Resolve(station, f.Path), f))) throw new Exception("Installed file hash mismatch.");
AssertStationData();
if (await client.CheckAsync(new Version(live.Version), channel) != null) throw new Exception("Equal version offered update.");
Console.WriteLine("PASS actual updater downloaded and hash-verified all changed public blobs, installed transactionally in synthetic station, preserved config/calibration/standards/unknown files, and skips equal version. No real station touched.");
var catalog = await client.GetCatalogAsync();
if (catalog.StableVersion != old.Version && betaMode || catalog.StableVersion != live.Version && !betaMode
    || betaMode && catalog.BetaVersion != live.Version || !catalog.Versions.Any(v => v.Version == old.Version) || !catalog.Versions.Any(v => v.Version == live.Version))
    throw new Exception("Live signed catalog does not list the expected stable/beta pointers.");
Console.WriteLine($"PASS live signed catalog: stable {catalog.StableVersion}, beta {catalog.BetaVersion ?? "none"}, {catalog.Versions.Length} versions.");
// Downgrade back to the previous release from the signed archive (the beta "not OK" path; also proves rollback for stable).
UpdateInstaller.Cleanup(station);
var back = await client.GetVersionAsync(old.Version);
if (!SameManifest(back, old)) throw new Exception("Archived previous manifest differs from previous feed.");
var downgrade = UpdateClient.CalculateChanges(back, station);
await client.DownloadAsync(downgrade, station);
UpdateInstaller.Apply(downgrade, station, new Version(live.Version), () => false);
if (!old.Files.All(f => UpdateSignature.Matches(UpdatePaths.Resolve(station, f.Path), f))) throw new Exception("Downgraded file hash mismatch.");
AssertStationData();
Console.WriteLine($"PASS downgrade {live.Version} -> {old.Version} from signed archive: {downgrade.Files.Count} files / {downgrade.TotalBytes} bytes, station data preserved.");
using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
string packagePath = Path.Combine(expectedFeed, "packages", live.Version, "SoncaAudioInspector-win-x64.zip");
using var wanted = File.OpenRead(packagePath);
string wantedHash = Convert.ToHexString(SHA256.HashData(wanted));
string downloaded = Path.Combine(output, "public-update-package.zip");
await using (var input = await http.GetStreamAsync(UpdateClient.DefaultFeed + "packages/" + live.Version + "/SoncaAudioInspector-win-x64.zip?sha256=" + wantedHash.ToLowerInvariant()))
await using (var file = File.Create(downloaded)) await input.CopyToAsync(file);
using var actual = File.OpenRead(downloaded);
string actualHash = Convert.ToHexString(SHA256.HashData(actual));
if (wantedHash != actualHash) throw new Exception("Public package ZIP mismatch.");
Console.WriteLine("PASS downloaded public ZIP SHA256=" + actualHash);

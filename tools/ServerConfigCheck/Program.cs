using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SoncaAudioInspector.Update;

// Offline checks for the mandatory signed server config (no network, test-only keys).
// Usage: dotnet ServerConfigCheck.dll <artifact>/checks/<new-dir>
string root = Path.GetFullPath(args.Single());
if (Directory.Exists(root)) throw new IOException("Output must be new.");
Directory.CreateDirectory(root);
var passed = new List<string>();
void Check(bool ok, string text)
{
    if (!ok) throw new Exception("FAIL " + text);
    passed.Add("PASS " + text); Console.WriteLine("PASS " + text);
    File.WriteAllLines(Path.Combine(root, "checks.txt"), passed);
}

using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
string privatePem = key.ExportECPrivateKeyPem(), publicPem = key.ExportSubjectPublicKeyInfoPem();
byte[] Config(string marker) => Encoding.UTF8.GetBytes("{\n  \"models\": [ { \"model\": \"" + marker + "\", \"testItems\": { \"InOut\": { \"Tests\": [] } } } ]\n}\n");
(byte[] Manifest, string Signature) Sign(byte[] content, long serial, string? signer = null)
{
    var manifest = new ServerConfigManifest(1, ServerConfigManifest.ExpectedApp, ServerConfigManifest.ExpectedKind, serial,
        DateTimeOffset.UtcNow, Convert.ToHexStringLower(SHA256.HashData(content)), content.LongLength, "test");
    byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, ServerConfigManifest.JsonOptions);
    return (bytes, UpdateSignature.Sign(bytes, signer ?? privatePem));
}
string Dir(string name) { string d = Path.Combine(root, name); Directory.CreateDirectory(d); return d; }

async Task<ServerConfigState> Run(FakeServer server, string station, bool admin = false, string? extraTarget = null)
{
    using var client = new ServerConfigClient(new Uri("https://offline.invalid/config/"), server, publicPem);
    var store = new ServerConfigStore(Path.Combine(station, "server-config"), publicPem);
    var targets = new List<string> { Path.Combine(station, "user", "checking_config.json") };
    if (extraTarget != null) targets.Add(extraTarget);
    return await ServerConfigSync.RunAsync(client, store, targets, admin);
}
byte[] Read(string station) => File.ReadAllBytes(Path.Combine(station, "user", "checking_config.json"));

// 1. Online: exact server bytes applied, cache saved, pointer cache-busted, content by hash.
string s1 = Dir("station1");
byte[] v1 = Config("SERVER-V1"); var (m1, sig1) = Sign(v1, 100);
var server = new FakeServer(m1, sig1, v1);
var state = await Run(server, s1);
Check(state.Mode == ServerConfigMode.Online && state.Manifest!.Serial == 100 && Read(s1).SequenceEqual(v1), "online: signed config applied byte-for-byte");
Check(server.Requests.Where(r => r.Contains("latest.json")).All(r => r.Contains("?t=")) && server.Requests.Any(r => r.EndsWith("/files/" + Convert.ToHexStringLower(SHA256.HashData(v1)) + ".json")),
    "pointer requests are cache-busted; content addressed by SHA-256");

// 2. Local edit is overwritten at next start (mandatory, unlike app updates).
File.WriteAllText(Path.Combine(s1, "user", "checking_config.json"), "{\"models\":[{\"model\":\"LOCAL-EDIT\"}]}");
state = await Run(server, s1);
Check(state.Mode == ServerConfigMode.Online && Read(s1).SequenceEqual(v1), "station edit replaced by server file at next start");

// 3. Offline: last verified server copy re-applied, warning state.
File.WriteAllText(Path.Combine(s1, "user", "checking_config.json"), "{\"models\":[{\"model\":\"LOCAL-EDIT\"}]}");
state = await Run(FakeServer.Down(), s1);
Check(state.Mode == ServerConfigMode.OfflineCached && state.AllowsMeasurement && state.Manifest!.Serial == 100 && Read(s1).SequenceEqual(v1),
    "offline: cached verified server copy re-applied with OFFLINE state");

// 4. Never verified + offline: Unavailable, measurement blocked, file untouched.
string s4 = Dir("station4");
Directory.CreateDirectory(Path.Combine(s4, "user")); File.WriteAllText(Path.Combine(s4, "user", "checking_config.json"), "{\"models\":[{\"model\":\"LOCAL\"}]}");
state = await Run(FakeServer.Down(), s4);
Check(state.Mode == ServerConfigMode.Unavailable && !state.AllowsMeasurement && File.ReadAllText(Path.Combine(s4, "user", "checking_config.json")).Contains("LOCAL"),
    "no verified copy + offline: Unavailable (measurement blocked)");

// 5. Tamper / wrong key / wrong hash rejected (falls back to verified cache).
byte[] tampered = m1.ToArray(); tampered[tampered.Length / 2] ^= 1;
state = await Run(new FakeServer(tampered, sig1, v1), s1);
Check(state.Mode == ServerConfigMode.OfflineCached && Read(s1).SequenceEqual(v1), "tampered manifest rejected; cache used");
byte[] evil = Config("EVIL"); var (mEvil, sigEvil) = Sign(evil, 999, wrongKey.ExportECPrivateKeyPem());
state = await Run(new FakeServer(mEvil, sigEvil, evil), s1);
Check(state.Mode == ServerConfigMode.OfflineCached && Read(s1).SequenceEqual(v1), "config signed by another key rejected");
var (mHash, sigHash) = Sign(Config("A"), 300);
state = await Run(new FakeServer(mHash, sigHash, Config("B")), s1);
Check(state.Mode == ServerConfigMode.OfflineCached && Read(s1).SequenceEqual(v1), "content not matching signed SHA-256 rejected");
byte[] noModels = Encoding.UTF8.GetBytes("{\"models\":[]}"); var (mEmpty, sigEmpty) = Sign(noModels, 301);
state = await Run(new FakeServer(mEmpty, sigEmpty, noModels), s1);
Check(state.Mode == ServerConfigMode.OfflineCached, "signed config without models rejected");

// 6. Rollback below cached serial refused; higher serial accepted.
byte[] old = Config("OLD"); var (mOld, sigOld) = Sign(old, 50);
state = await Run(new FakeServer(mOld, sigOld, old), s1);
Check(state.Mode == ServerConfigMode.OfflineCached && Read(s1).SequenceEqual(v1) && state.Message.Contains("rollback"), "lower serial than verified cache refused (anti-rollback)");
byte[] v2 = Config("SERVER-V2"); var (m2, sig2) = Sign(v2, 200);
state = await Run(new FakeServer(m2, sig2, v2), s1);
Check(state.Mode == ServerConfigMode.Online && state.Manifest!.Serial == 200 && Read(s1).SequenceEqual(v2), "newer serial applied");

// 7. Corrupted cache is ignored (re-verified on every use).
string s7 = Dir("station7");
await Run(new FakeServer(m2, sig2, v2), s7);
File.WriteAllText(Path.Combine(s7, "server-config", "checking_config.json"), "{\"models\":[{\"model\":\"CORRUPT\"}]}");
state = await Run(FakeServer.Down(), s7);
Check(state.Mode == ServerConfigMode.Unavailable, "tampered cache fails verification; not used");

// 8. Admin station keeps a differing local file (until published); production default never trusts the test key.
string s8 = Dir("station8");
Directory.CreateDirectory(Path.Combine(s8, "user")); File.WriteAllText(Path.Combine(s8, "user", "checking_config.json"), "{\"models\":[{\"model\":\"ADMIN-DRAFT\"}]}");
state = await Run(new FakeServer(m2, sig2, v2), s8, admin: true);
Check(state.Mode == ServerConfigMode.AdminLocal && File.ReadAllText(Path.Combine(s8, "user", "checking_config.json")).Contains("ADMIN-DRAFT"), "admin mode keeps local draft and reports it");
bool rejected = false;
try { ServerConfigClient.VerifyManifest(m2, sig2); } catch (CryptographicException) { rejected = true; }
Check(rejected, "embedded production public key rejects test-key signature");

// 9. Portable copy not writable is best-effort; station data copy still applied.
string s9 = Dir("station9"); string blocker = Dir("station9/portable-is-a-directory");
state = await Run(new FakeServer(m2, sig2, v2), s9, extraTarget: blocker);
Check(state.Mode == ServerConfigMode.Online && Read(s9).SequenceEqual(v2), "unwritable portable copy does not block applying the server config");

Console.WriteLine($"ALL {passed.Count} SERVER CONFIG CHECKS PASSED");

sealed class FakeServer : HttpMessageHandler
{
    private readonly byte[]? _manifest, _content; private readonly string? _signature;
    public List<string> Requests { get; } = [];
    public FakeServer(byte[] manifest, string signature, byte[] content) { _manifest = manifest; _signature = signature; _content = content; }
    private FakeServer() { }
    public static FakeServer Down() => new();
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        string url = request.RequestUri!.ToString(); Requests.Add(url);
        if (_manifest == null) throw new HttpRequestException("offline");
        string path = request.RequestUri.AbsolutePath;
        byte[]? body = path.EndsWith("/latest.json") ? _manifest : path.EndsWith("/latest.json.sig") ? Encoding.UTF8.GetBytes(_signature!)
            : path.Contains("/files/") ? _content : null;
        return Task.FromResult(body == null ? new HttpResponseMessage(HttpStatusCode.NotFound) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) });
    }
}

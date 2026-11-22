using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BmwDecode.Core.Data;

namespace BmwDecode.Core.Tests;

public class DataDownloaderTests
{
    /// <summary>In-memory data server with basic auth, like the Caddy site.</summary>
    private sealed class FakeServer : HttpMessageHandler
    {
        public Dictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        public string Password { get; set; } = "secret";
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{DataDownloader.UserName}:{Password}"));
            if (request.Headers.Authorization?.Parameter != expected)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized));
            }

            var name = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Add(name);
            return Task.FromResult(Files.TryGetValue(name, out var body)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void Publish(string version, Func<string, string>? corrupt = null)
        {
            var files = Files.Where(kv => kv.Key != DataDownloader.ManifestName)
                .Select(kv => $$"""{"path":"{{kv.Key}}","size":{{kv.Value.Length}},"sha256":"{{corrupt?.Invoke(kv.Key) ?? Convert.ToHexString(SHA256.HashData(kv.Value))}}","extract":{{(kv.Key.EndsWith(".zip") ? "true" : "false")}}}""");
            // Shaped like the PowerShell upload script's ConvertTo-Json output.
            Files[DataDownloader.ManifestName] = Encoding.UTF8.GetBytes(
                $$"""{ "version": "{{version}}", "built": "2026-11-22T18:00:00.0000000Z", "files": [ {{string.Join(",", files)}} ] }""");
        }
    }

    private static byte[] EcuZip(params string[] names)
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var n in names)
            {
                using var w = new StreamWriter(zip.CreateEntry(n).Open());
                w.Write("sgbd " + n);
            }
        }

        return ms.ToArray();
    }

    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bmwdecode-dl-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task FirstSyncDownloadsAndUnpacksThenOnlyChangesAreFetched()
    {
        var server = new FakeServer();
        server.Files["ecu.zip"] = EcuZip("d60m57a0.prg", "e89x.prg");
        server.Files["sgbd.pack"] = [1, 2, 3, 4];
        server.Publish("1");
        var target = TempDir();
        try
        {
            using var dl = new DataDownloader("https://tinker.example", "secret", server);
            var released = new List<string>();
            var first = await dl.SyncAsync(target, released.Add);
            Assert.Equal(2, first.Updated.Count);
            Assert.True(File.Exists(Path.Combine(target, "Ecu", "d60m57a0.prg")));
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(Path.Combine(target, "sgbd.pack")));
            Assert.Contains(Path.Combine(target, "sgbd.pack"), released);

            server.Requests.Clear();
            var second = await dl.SyncAsync(target);
            Assert.Empty(second.Updated);
            Assert.Equal(new[] { DataDownloader.ManifestName }, server.Requests.ToArray());

            // Only the pack changes: the Ecu folder is not downloaded again.
            server.Files["sgbd.pack"] = [9, 9];
            server.Publish("2");
            server.Requests.Clear();
            var third = await dl.SyncAsync(target);
            Assert.Equal(new[] { "sgbd.pack" }, third.Updated.ToArray());
            Assert.DoesNotContain("ecu.zip", server.Requests);
            Assert.Equal("2", DataDownloader.ReadLocalManifest(target)!.Version);
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public async Task WrongPasswordAndBadChecksumAreReported()
    {
        var server = new FakeServer();
        server.Files["sgbd.pack"] = [1, 2, 3];
        server.Publish("1");
        var target = TempDir();
        try
        {
            using (var wrong = new DataDownloader("https://tinker.example", "nope", server))
            {
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => wrong.SyncAsync(target));
            }

            server.Publish("1", corrupt: _ => new string('0', 64));
            using var dl = new DataDownloader("https://tinker.example", "secret", server);
            await Assert.ThrowsAsync<InvalidDataException>(() => dl.SyncAsync(target));
            Assert.False(File.Exists(Path.Combine(target, "sgbd.pack")));
        }
        finally
        {
            Directory.Delete(target, recursive: true);
        }
    }

    [Fact]
    public void ManifestOfAFolderListsHashesAndZips()
    {
        var dir = TempDir();
        try
        {
            File.WriteAllBytes(Path.Combine(dir, "ecu.zip"), EcuZip("a.prg"));
            File.WriteAllBytes(Path.Combine(dir, "sgbd.pack"), [5]);
            var m = DataDownloader.CreateManifest(dir, "v");
            Assert.Equal(2, m.Files.Count);
            Assert.True(m.Files.Single(f => f.Path == "ecu.zip").Extract);
            Assert.Equal(Convert.ToHexString(SHA256.HashData(new byte[] { 5 })), m.Files.Single(f => f.Path == "sgbd.pack").Sha256);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

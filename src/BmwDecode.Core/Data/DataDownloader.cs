using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmwDecode.Core.Data;

/// <summary>
/// Downloads the private BMW data (the Ecu folder as ecu.zip, and sgbd.pack) from the owner's own server, so a
/// fresh install needs no INPA or ISTA. The server is password-protected (HTTP basic auth over HTTPS); the
/// password is the owner's and is never part of the app or the repository.
///
/// Server layout: manifest.json listing every file with its size and SHA-256, plus the files themselves.
/// Only files whose hash changed are downloaded again; every file is verified before it replaces the old one.
/// </summary>
public sealed class DataDownloader : IDisposable
{
    public const string DefaultServer = "https://tinker.ftw.co.ke";
    public const string UserName = "bmwdecode";
    public const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly HttpClient _http;
    private readonly Uri _base;

    public DataDownloader(string server, string password, HttpMessageHandler? handler = null)
    {
        _base = new Uri(server.TrimEnd('/') + "/");
        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        _http.Timeout = TimeSpan.FromMinutes(30);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{UserName}:{password}")));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("bmwDecode");
    }

    public void Dispose() => _http.Dispose();

    /// <summary>The server's manifest. Throws <see cref="UnauthorizedAccessException"/> for a wrong password.</summary>
    public async Task<DataManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync(new Uri(_base, ManifestName), cancellationToken).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("The data server did not accept the password.");
        }

        response.EnsureSuccessStatusCode();
        await using var s = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<DataManifest>(s, s_json, cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("The data server returned an empty manifest.");
    }

    /// <summary>
    /// Brings <paramref name="target"/> up to date with the server: Data/sgbd.pack and Ecu/ (from ecu.zip).
    /// <paramref name="release"/> is called with a file's final path just before it is replaced, so the caller
    /// can close it (the open data pack). Returns what changed.
    /// </summary>
    public async Task<DataSyncResult> SyncAsync(
        string target, Action<string>? release = null, IProgress<DataProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);
        var remote = await GetManifestAsync(cancellationToken).ConfigureAwait(false);
        var local = ReadLocalManifest(target);
        var changed = remote.Files
            .Where(f => !IsCurrent(target, f, local))
            .ToList();

        var total = changed.Sum(f => f.Size);
        long done = 0;
        var updated = new List<string>();
        foreach (var file in changed)
        {
            var temp = Path.Combine(target, file.Path + ".download");
            Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
            await DownloadAsync(file, temp, p => progress?.Report(new DataProgress(file.Path, done + p, total)), cancellationToken)
                .ConfigureAwait(false);
            done += file.Size;

            if (file.Extract)
            {
                // ecu.zip -> Ecu/: unpack beside the old folder, then swap, so a failure leaves the old one intact.
                var folder = Path.Combine(target, Path.GetFileNameWithoutExtension(file.Path) is var n && n.Equals("ecu", StringComparison.OrdinalIgnoreCase) ? "Ecu" : n);
                var fresh = folder + ".new";
                if (Directory.Exists(fresh))
                {
                    Directory.Delete(fresh, recursive: true);
                }

                progress?.Report(new DataProgress($"Unpacking {file.Path}", done, total));
                ZipFile.ExtractToDirectory(temp, fresh, overwriteFiles: true);
                // Archives made from a folder may contain that folder as their only entry: use its contents.
                var inner = Directory.GetDirectories(fresh);
                var root = inner.Length == 1 && Directory.GetFiles(fresh).Length == 0 ? inner[0] : fresh;
                release?.Invoke(folder);
                var old = folder + ".old";
                if (Directory.Exists(old))
                {
                    Directory.Delete(old, recursive: true);
                }

                if (Directory.Exists(folder))
                {
                    Directory.Move(folder, old);
                }

                Directory.Move(root, folder);
                if (Directory.Exists(fresh))
                {
                    Directory.Delete(fresh, recursive: true);
                }

                if (Directory.Exists(old))
                {
                    Directory.Delete(old, recursive: true);
                }

                File.Delete(temp);
            }
            else
            {
                var final = Path.Combine(target, file.Path);
                release?.Invoke(final);
                File.Move(temp, final, overwrite: true);
            }

            updated.Add(file.Path);
            // Record progress after every file so an interrupted update resumes where it stopped.
            WriteLocalManifest(target, remote with { Files = MergeFiles(local, remote, updated) });
        }

        WriteLocalManifest(target, remote);
        return new DataSyncResult(remote, updated);
    }

    private async Task DownloadAsync(DataFile file, string temp, Action<long> progress, CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(new Uri(_base, file.Path), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UnauthorizedAccessException("The data server did not accept the password.");
        }

        response.EnsureSuccessStatusCode();
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
        await using (var output = File.Create(temp))
        {
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = await input.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, n), cancellationToken).ConfigureAwait(false);
                sha.AppendData(buffer, 0, n);
                read += n;
                progress(read);
            }
        }

        var hash = Convert.ToHexString(sha.GetHashAndReset());
        if (!hash.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException($"{file.Path} did not download correctly (checksum mismatch). Try again.");
        }
    }

    /// <summary>Unchanged on the server and still present locally.</summary>
    private static bool IsCurrent(string target, DataFile file, DataManifest? local)
    {
        var known = local?.Files.FirstOrDefault(f => f.Path.Equals(file.Path, StringComparison.OrdinalIgnoreCase));
        if (known is null || !known.Sha256.Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return file.Extract
            ? Directory.Exists(Path.Combine(target, "Ecu"))
            : File.Exists(Path.Combine(target, file.Path));
    }

    private static List<DataFile> MergeFiles(DataManifest? local, DataManifest remote, List<string> updated)
    {
        var files = (local?.Files ?? []).Where(f => !updated.Contains(f.Path, StringComparer.OrdinalIgnoreCase)).ToList();
        files.AddRange(remote.Files.Where(f => updated.Contains(f.Path, StringComparer.OrdinalIgnoreCase)));
        return files;
    }

    public static DataManifest? ReadLocalManifest(string target)
    {
        var p = Path.Combine(target, ManifestName);
        try
        {
            return File.Exists(p) ? JsonSerializer.Deserialize<DataManifest>(File.ReadAllText(p), s_json) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            return null;
        }
    }

    private static void WriteLocalManifest(string target, DataManifest manifest) =>
        File.WriteAllText(Path.Combine(target, ManifestName), JsonSerializer.Serialize(manifest, s_json));

    /// <summary>Builds a manifest for a folder of files (used by the upload tool and tests).</summary>
    public static DataManifest CreateManifest(string folder, string version)
    {
        var files = Directory.EnumerateFiles(folder)
            .Where(f => !Path.GetFileName(f).Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .Select(f =>
            {
                using var s = File.OpenRead(f);
                var name = Path.GetFileName(f);
                return new DataFile(name, s.Length, Convert.ToHexString(SHA256.HashData(s)),
                    name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            })
            .ToList();
        return new DataManifest(version, DateTimeOffset.UtcNow, files);
    }
}

public sealed record DataManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("built")] DateTimeOffset Built,
    [property: JsonPropertyName("files")] List<DataFile> Files);

/// <param name="Extract">A zip to unpack into a folder of the same name (ecu.zip becomes Ecu/).</param>
public sealed record DataFile(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("size")] long Size,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("extract")] bool Extract);

public sealed record DataProgress(string Item, long Done, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Done / Total, 0, 1) : 0;
}

public sealed record DataSyncResult(DataManifest Manifest, IReadOnlyList<string> Updated);

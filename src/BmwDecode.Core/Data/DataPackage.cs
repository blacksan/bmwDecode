using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmwDecode.Core.Data;

/// <summary>
/// The BMW data bundle (bmwDecode-data.zip) that makes an install self-sufficient without INPA or ISTA:
/// ecu.zip (the merged Ecu folder), sgbd.pack (the built-in data pack) and manifest.json (size and SHA-256 of each).
/// Built on the PC that has INPA/ISTA (installer\make-data-zip.ps1) and imported on first start of a new install.
/// It holds BMW's files: keep it to your own machines.
/// </summary>
public static class DataPackage
{
    public const string FileName = "bmwDecode-data.zip";
    public const string ManifestName = "manifest.json";

    private static readonly JsonSerializerOptions s_json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Installs a bundle into <paramref name="target"/>: sgbd.pack and Ecu/ (from ecu.zip). Every file is checked against
    /// the manifest before it replaces the old one; the old Ecu folder is only swapped out once the new one is complete.
    /// <paramref name="release"/> is called with a path just before it is replaced (to close the open data pack).
    /// </summary>
    public static DataManifest Import(string bundle, string target, Action<string>? release = null, IProgress<DataProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(target);
        using var zip = ZipFile.OpenRead(bundle);
        var manifestEntry = zip.Entries.FirstOrDefault(e => e.Name.Equals(ManifestName, StringComparison.OrdinalIgnoreCase))
                            ?? throw new InvalidDataException($"{Path.GetFileName(bundle)} is not a bmwDecode data file (no {ManifestName}).");
        DataManifest manifest;
        using (var s = manifestEntry.Open())
        {
            manifest = JsonSerializer.Deserialize<DataManifest>(s, s_json)
                       ?? throw new InvalidDataException("The data file's manifest is empty.");
        }

        var total = manifest.Files.Sum(f => f.Size);
        long done = 0;
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = zip.Entries.FirstOrDefault(e => e.FullName.Equals(file.Path, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidDataException($"{file.Path} is missing from {Path.GetFileName(bundle)}.");
            var temp = Path.Combine(target, Path.GetFileName(file.Path) + ".import");
            var start = done;
            Copy(entry, temp, file, n => progress?.Report(new DataProgress($"Copying {file.Path}", start + n, total)), cancellationToken);
            done += file.Size;
            progress?.Report(new DataProgress(file.Extract ? $"Unpacking {file.Path}" : $"Installing {file.Path}", done, total));
            Install(temp, file, target, release);
        }

        File.WriteAllText(Path.Combine(target, ManifestName), JsonSerializer.Serialize(manifest, s_json));
        return manifest;
    }

    /// <summary>The installed bundle's manifest, or null.</summary>
    public static DataManifest? Installed(string target)
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

    /// <summary>A bmwDecode-data.zip in the usual places (Desktop, Downloads, next to the app), newest first.</summary>
    public static string? FindBundle()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(home, "Downloads"),
            Path.Combine(home, "Desktop"),
            AppContext.BaseDirectory,
        };
        return folders.Where(Directory.Exists)
            .SelectMany(f => Directory.EnumerateFiles(f, "bmwDecode-data*.zip"))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }

    private static void Copy(ZipArchiveEntry entry, string temp, DataFile file, Action<long> progress, CancellationToken cancellationToken)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using (var input = entry.Open())
        using (var output = File.Create(temp))
        {
            var buffer = new byte[1 << 16];
            long read = 0;
            int n;
            while ((n = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                output.Write(buffer, 0, n);
                sha.AppendData(buffer, 0, n);
                read += n;
                progress(read);
            }
        }

        if (!Convert.ToHexString(sha.GetHashAndReset()).Equals(file.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException($"{file.Path} in the data file is damaged (checksum mismatch). Copy the zip again.");
        }
    }

    private static void Install(string temp, DataFile file, string target, Action<string>? release)
    {
        if (!file.Extract)
        {
            var final = Path.Combine(target, Path.GetFileName(file.Path));
            release?.Invoke(final);
            File.Move(temp, final, overwrite: true);
            return;
        }

        // ecu.zip -> Ecu/: unpack beside the old folder, then swap, so a failure leaves the old one intact.
        var name = Path.GetFileNameWithoutExtension(file.Path);
        var folder = Path.Combine(target, name.Equals("ecu", StringComparison.OrdinalIgnoreCase) ? "Ecu" : name);
        var fresh = folder + ".new";
        var old = folder + ".old";
        foreach (var d in new[] { fresh, old })
        {
            if (Directory.Exists(d))
            {
                Directory.Delete(d, recursive: true);
            }
        }

        ZipFile.ExtractToDirectory(temp, fresh, overwriteFiles: true);
        // Archives made from a folder may contain that folder as their only entry: use its contents.
        var inner = Directory.GetDirectories(fresh);
        var root = inner.Length == 1 && Directory.GetFiles(fresh).Length == 0 ? inner[0] : fresh;
        release?.Invoke(folder);
        if (Directory.Exists(folder))
        {
            Directory.Move(folder, old);
        }

        Directory.Move(root, folder);
        foreach (var d in new[] { fresh, old })
        {
            if (Directory.Exists(d))
            {
                Directory.Delete(d, recursive: true);
            }
        }

        File.Delete(temp);
    }

    /// <summary>Writes a bundle from an Ecu folder and a data pack (used by tests; Windows uses make-data-zip.ps1).</summary>
    public static DataManifest Create(string bundle, string ecuFolder, string? pack, string version)
    {
        var work = Path.Combine(Path.GetTempPath(), "bmwdecode-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var files = new List<DataFile>();
            var ecuZip = Path.Combine(work, "ecu.zip");
            ZipFile.CreateFromDirectory(ecuFolder, ecuZip, CompressionLevel.Optimal, includeBaseDirectory: false);
            files.Add(Describe(ecuZip, extract: true));
            if (pack is not null)
            {
                var p = Path.Combine(work, SgbdPack.FileName);
                File.Copy(pack, p);
                files.Add(Describe(p, extract: false));
            }

            var manifest = new DataManifest(version, DateTimeOffset.UtcNow, files);
            File.WriteAllText(Path.Combine(work, ManifestName), JsonSerializer.Serialize(manifest, s_json));
            if (File.Exists(bundle))
            {
                File.Delete(bundle);
            }

            using var zip = ZipFile.Open(bundle, ZipArchiveMode.Create);
            foreach (var f in Directory.GetFiles(work))
            {
                zip.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.NoCompression);
            }

            return manifest;
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    private static DataFile Describe(string path, bool extract)
    {
        using var s = File.OpenRead(path);
        return new DataFile(Path.GetFileName(path), s.Length, Convert.ToHexString(SHA256.HashData(s)), extract);
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

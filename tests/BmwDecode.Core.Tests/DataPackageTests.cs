using System.IO.Compression;
using BmwDecode.Core.Data;

namespace BmwDecode.Core.Tests;

public class DataPackageTests
{
    private static string TempDir() => Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "bmwdecode-pkg-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public void BundleImportsEcuFolderAndPackAndReplacesOlderData()
    {
        var root = TempDir();
        try
        {
            var ecu = Directory.CreateDirectory(Path.Combine(root, "src", "Ecu")).FullName;
            File.WriteAllText(Path.Combine(ecu, "d60m57a0.prg"), "v1");
            var pack = Path.Combine(root, "src", "sgbd.pack");
            File.WriteAllBytes(pack, [1, 2, 3]);
            var bundle = Path.Combine(root, DataPackage.FileName);
            DataPackage.Create(bundle, ecu, pack, "1");

            var target = Path.Combine(root, "Data");
            var released = new List<string>();
            var m = DataPackage.Import(bundle, target, released.Add);
            Assert.Equal("1", m.Version);
            Assert.Equal("v1", File.ReadAllText(Path.Combine(target, "Ecu", "d60m57a0.prg")));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(target, "sgbd.pack")));
            Assert.Contains(Path.Combine(target, "sgbd.pack"), released);
            Assert.Equal("1", DataPackage.Installed(target)!.Version);

            // A newer bundle replaces the Ecu folder completely (removed files go too).
            File.Delete(Path.Combine(ecu, "d60m57a0.prg"));
            File.WriteAllText(Path.Combine(ecu, "e89x.prg"), "v2");
            DataPackage.Create(bundle, ecu, pack, "2");
            DataPackage.Import(bundle, target);
            Assert.False(File.Exists(Path.Combine(target, "Ecu", "d60m57a0.prg")));
            Assert.True(File.Exists(Path.Combine(target, "Ecu", "e89x.prg")));
            Assert.Equal("2", DataPackage.Installed(target)!.Version);
            Assert.False(Directory.Exists(Path.Combine(target, "Ecu.new")));
            Assert.False(Directory.Exists(Path.Combine(target, "Ecu.old")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void DamagedOrForeignZipsAreRejected()
    {
        var root = TempDir();
        try
        {
            var foreign = Path.Combine(root, "other.zip");
            using (var z = ZipFile.Open(foreign, ZipArchiveMode.Create))
            {
                using var w = new StreamWriter(z.CreateEntry("readme.txt").Open());
                w.Write("hi");
            }

            Assert.Throws<InvalidDataException>(() => DataPackage.Import(foreign, Path.Combine(root, "Data")));

            // Manifest says one hash, file has another.
            var bad = Path.Combine(root, "bad.zip");
            using (var z = ZipFile.Open(bad, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(z.CreateEntry("manifest.json").Open()))
                {
                    w.Write("""{"version":"1","built":"2026-11-22T00:00:00Z","files":[{"path":"sgbd.pack","size":3,"sha256":"00","extract":false}]}""");
                }

                using var s = z.CreateEntry("sgbd.pack").Open();
                s.Write([1, 2, 3]);
            }

            Assert.Throws<InvalidDataException>(() => DataPackage.Import(bad, Path.Combine(root, "Data")));
            Assert.False(File.Exists(Path.Combine(root, "Data", "sgbd.pack")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}

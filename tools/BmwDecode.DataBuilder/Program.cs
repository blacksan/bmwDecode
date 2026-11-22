#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using BmwDecode.Core.Data;
using BmwDecode.Core.Translation;
using BmwDecode.DataBuilder;

// bmwDecode data builder: reads every SGBD in one or more Ecu folders and writes sgbd.pack.
//
//   BmwDecode.DataBuilder <output sgbd.pack> <Ecu folder> [<more Ecu folders>...] [--ista <EcuFunctions.zip or folder>]
//
// --ista adds an ECU functions export (official English fault texts, labelled live values, type keys, VIN ranges).
//
// When the same SGBD name exists in several folders, the folder listed LAST wins (pass INPA first, ISTA last).
// The output is derived from BMW's SGBD files: keep it with the private build, never commit or publish it.

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: BmwDecode.DataBuilder <output sgbd.pack> <Ecu folder> [<more Ecu folders>...]");
    return 2;
}

string? istaSource = null;
var positional = new List<string>();
for (var i = 0; i < args.Length; i++)
{
    if (args[i].Equals("--ista", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
    {
        istaSource = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

if (positional.Count < 2)
{
    Console.Error.WriteLine("usage: BmwDecode.DataBuilder <output sgbd.pack> <Ecu folder> [...] [--ista <EcuFunctions.zip>]");
    return 2;
}

var output = Path.GetFullPath(positional[0]);
var folders = positional.Skip(1).Where(Directory.Exists).Select(Path.GetFullPath).ToList();
if (folders.Count == 0)
{
    Console.Error.WriteLine("No Ecu folder found.");
    return 2;
}

// Last folder wins for duplicate names.
var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
foreach (var folder in folders)
{
    foreach (var f in Directory.EnumerateFiles(folder))
    {
        var ext = Path.GetExtension(f).ToLowerInvariant();
        if (ext is ".prg" or ".grp")
        {
            files[Path.GetFileNameWithoutExtension(f) + ext] = f;
        }
    }
}

// One pack entry per name: if both NAME.prg and NAME.grp exist, keep the .prg (EDIABAS prefers it too).
foreach (var grp in files.Keys.Where(k => k.EndsWith(".grp", StringComparison.OrdinalIgnoreCase)).ToList())
{
    if (files.ContainsKey(Path.ChangeExtension(grp, ".prg")))
    {
        files.Remove(grp);
    }
}

Console.WriteLine($"{files.Count} SGBD files in {string.Join(", ", folders)}");
Directory.CreateDirectory(Path.GetDirectoryName(output)!);
var temp = output + ".tmp";
var sw = Stopwatch.StartNew();

using (var stream = File.Create(temp))
{
    var writer = new SgbdPackWriter(stream, string.Join(";", folders.Select(Path.GetFileName)));
    var done = 0;
    foreach (var group in files.Values.GroupBy(Path.GetDirectoryName))
    {
        using var extractor = new SgbdExtractor(group.Key!);
        foreach (var file in group.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
            try
            {
                var sgbd = extractor.Extract(file);
                if (sgbd.Jobs.Count == 0 && sgbd.Tables.Count == 0)
                {
                    writer.Failed(name, "no jobs or tables");
                }
                else
                {
                    writer.Add(sgbd);
                }
            }
            catch (Exception ex)
            {
                writer.Failed(name, ex.GetType().Name + ": " + ex.Message.Split('\n')[0]);
            }

            if (++done % 100 == 0)
            {
                Console.WriteLine($"  {done}/{files.Count}  ({sw.Elapsed:mm\\:ss})");
            }
        }
    }

    if (istaSource is not null)
    {
        if (!File.Exists(istaSource) && !Directory.Exists(istaSource))
        {
            Console.Error.WriteLine($"ECU functions export not found: {istaSource}");
            return 2;
        }

        Console.WriteLine($"Importing ECU functions export {istaSource}...");
        using var ista = new IstaImporter(istaSource);
        ista.ImportInto(writer, Console.WriteLine);
    }

    Console.WriteLine($"Translating {writer.Texts.Count} distinct texts...");
    var translator = Translator.CreateDefault();
    writer.Complete(translator.Translate);

    var m = writer.Manifest;
    Console.WriteLine($"Packed {m.SgbdCount} SGBDs, {m.JobCount} jobs, {m.TableCount} tables, {m.TranslationCount} translations; {m.Failed.Count} failed.");
    foreach (var f in m.Failed.Take(20))
    {
        Console.WriteLine("  failed " + f);
    }
}

File.Move(temp, output, overwrite: true);
Console.WriteLine($"Wrote {output} ({new FileInfo(output).Length / 1024.0 / 1024.0:F1} MB) in {sw.Elapsed:mm\\:ss}");
return 0;

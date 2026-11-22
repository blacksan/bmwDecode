#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BmwDecode.Core.Data;
using EdiabasLib;

namespace BmwDecode.DataBuilder;

/// <summary>
/// Reads one SGBD (.prg / .grp) completely through EdiabasLib's virtual jobs (_JOBS, _JOBCOMMENTS, _ARGUMENTS,
/// _RESULTS, _TABLES, _TABLE, _VERSIONINFO). No vehicle or interface is needed: the init job is suppressed.
/// </summary>
public sealed class SgbdExtractor : IDisposable
{
    private readonly EdiabasNet _ed;

    static SgbdExtractor()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SgbdExtractor(string ecuPath)
    {
        _ed = new EdiabasNet { EdInterfaceClass = new EdInterfaceObd { ComPort = "COM1" }, NoInitForVJobs = true };
        _ed.SetConfigProperty("EcuPath", ecuPath);
    }

    public PackedSgbd Extract(string file)
    {
        var name = Path.GetFileNameWithoutExtension(file).ToUpperInvariant();
        var isGroup = Path.GetExtension(file).Equals(".grp", StringComparison.OrdinalIgnoreCase);
        if (isGroup)
        {
            // ResolveSgbdFile would run the group's IDENT job against a car to pick a variant. Open the group
            // file itself instead: its jobs and tables are readable offline like any SGBD.
            _ed.SgbdFileName = Path.GetFileNameWithoutExtension(file).ToLowerInvariant() + EdiabasNet.GroupFileExt;
        }
        else
        {
            _ed.ResolveSgbdFile(file);
        }

        var sgbd = new PackedSgbd { Name = name, IsGroup = isGroup };

        foreach (var set in Run("_VERSIONINFO", string.Empty))
        {
            foreach (var kv in set)
            {
                if (Text(kv.Value) is { Length: > 0 } v)
                {
                    sgbd.Version[kv.Key] = v;
                }
            }
        }

        var jobNames = Run("_JOBS", string.Empty)
            .Select(s => s.TryGetValue("JOBNAME", out var v) ? Text(v) : null)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var job in jobNames)
        {
            sgbd.Jobs.Add(new PackedJob
            {
                Name = job,
                Comments = Run("_JOBCOMMENTS", job).SelectMany(s => Numbered(s, "JOBCOMMENT")).ToList(),
                Arguments = Parameters(Run("_ARGUMENTS", job), "ARG"),
                Results = Parameters(Run("_RESULTS", job), "RESULT"),
            });
        }

        var tableNames = Run("_TABLES", string.Empty)
            .Select(s => s.TryGetValue("TABLE", out var v) ? Text(v) : null)
            .Where(n => !string.IsNullOrEmpty(n))
            .Cast<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var table in tableNames)
        {
            var rows = Run("_TABLE", table)
                .Select(set => set
                    .Where(kv => kv.Key.StartsWith("COLUMN", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(kv => int.TryParse(kv.Key.AsSpan(6), out var i) ? i : int.MaxValue)
                    .Select(kv => Text(kv.Value) ?? string.Empty)
                    .ToArray())
                .Where(r => r.Length > 0)
                .ToList();
            if (rows.Count > 0)
            {
                sgbd.Tables[table] = rows;
            }
        }

        return sgbd;
    }

    public void Dispose() => _ed.Dispose();

    private List<Dictionary<string, EdiabasNet.ResultData>> Run(string vjob, string arg)
    {
        _ed.ArgBinary = null;
        _ed.ArgString = arg;
        _ed.ResultsRequests = string.Empty;
        try
        {
            _ed.ExecuteJob(vjob);
        }
        catch (EdiabasNet.EdiabasNetException)
        {
            return [];
        }

        // Set 0 is the system set (VARIANTE, JOBSTATUS, ...).
        return (_ed.ResultSets ?? []).Skip(1).ToList();
    }

    private static string? Text(EdiabasNet.ResultData v) => (v.OpData as string)?.Trim();

    private static IEnumerable<string> Numbered(Dictionary<string, EdiabasNet.ResultData> set, string prefix) =>
        set.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                        && int.TryParse(kv.Key.AsSpan(prefix.Length), out _))
            .OrderBy(kv => int.Parse(kv.Key.AsSpan(prefix.Length)))
            .Select(kv => Text(kv.Value))
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>();

    private static List<PackedParameter> Parameters(List<Dictionary<string, EdiabasNet.ResultData>> sets, string prefix)
    {
        var list = new List<PackedParameter>();
        foreach (var set in sets)
        {
            if (!set.TryGetValue(prefix, out var n) || Text(n) is not { Length: > 0 } name)
            {
                continue;
            }

            list.Add(new PackedParameter
            {
                Name = name,
                Type = set.TryGetValue(prefix + "TYPE", out var t) ? Text(t) ?? string.Empty : string.Empty,
                Comments = Numbered(set, prefix + "COMMENT").ToList(),
            });
        }

        return list;
    }
}

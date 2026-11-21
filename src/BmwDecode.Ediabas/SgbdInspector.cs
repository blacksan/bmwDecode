#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BmwDecode.Core.Diagnostics;
using EdiabasLib;

namespace BmwDecode.Ediabas;

/// <summary>
/// Reads SGBD metadata (_JOBS, _JOBCOMMENTS, _ARGUMENTS, _RESULTS) straight from the .prg files with no
/// vehicle connection, using EdiabasLib's virtual jobs with the init job suppressed.
/// </summary>
public sealed class SgbdInspector : ISgbdInspector, IDisposable
{
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, SgbdDescription> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, JobDescription> _jobCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly string _ecuPath;
    private EdiabasNet? _ediabas;

    static SgbdInspector()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public SgbdInspector(string ecuPath)
    {
        _ecuPath = ecuPath ?? string.Empty;
    }

    public bool IsAvailable => Directory.Exists(_ecuPath) && ListFiles().Count > 0;

    public IReadOnlyList<SgbdFile> ListFiles()
    {
        if (!Directory.Exists(_ecuPath))
        {
            return [];
        }

        return Directory.EnumerateFiles(_ecuPath)
            .Select(f => (Path: f, Ext: Path.GetExtension(f).ToLowerInvariant()))
            .Where(f => f.Ext is ".prg" or ".grp")
            .Select(f => new SgbdFile(Path.GetFileNameWithoutExtension(f.Path).ToUpperInvariant(), f.Ext == ".grp", f.Path))
            .DistinctBy(f => f.Name + f.IsGroup)
            .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public Task<SgbdDescription> DescribeAsync(string sgbd, CancellationToken cancellationToken = default) =>
        Task.Run(() => _cache.GetOrAdd(sgbd, Describe), cancellationToken);

    public Task<JobDescription> DescribeJobAsync(string sgbd, string job, CancellationToken cancellationToken = default) =>
        Task.Run(() => _jobCache.GetOrAdd($"{sgbd}|{job}", _ => DescribeJob(sgbd, job)), cancellationToken);

    public Task<IReadOnlyList<IReadOnlyList<string>>> ReadTableAsync(string sgbd, string table, CancellationToken cancellationToken = default) =>
        Task.Run(() => ReadTable(sgbd, table), cancellationToken);

    public IReadOnlyList<IReadOnlyList<string>> ReadTable(string sgbd, string table)
    {
        lock (_lock)
        {
            if (!File.Exists(Path.Combine(_ecuPath, sgbd + ".prg")) && !ListFiles().Any(f => f.Name.Equals(sgbd, StringComparison.OrdinalIgnoreCase)))
            {
                return [];
            }

            try
            {
                // _TABLE virtual job: one set per row (header first) with COLUMN0..n. Works without a connection.
                var sets = Run(Open(sgbd), "_TABLE", table);
                return sets
                    .Select(set => (IReadOnlyList<string>)set
                        .Where(kv => kv.Key.StartsWith("COLUMN", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(kv => int.TryParse(kv.Key.AsSpan(6), out var i) ? i : int.MaxValue)
                        .Select(kv => (kv.Value.OpData as string)?.Trim() ?? string.Empty)
                        .ToList())
                    .Where(row => row.Count > 0)
                    .ToList();
            }
            catch (DiagnosticException)
            {
                return [];
            }
        }
    }

    /// <summary>Synchronous variant used by tooling and tests.</summary>
    public SgbdDescription Describe(string sgbd)
    {
        lock (_lock)
        {
            var ed = Open(sgbd);
            var names = Run(ed, "_JOBS", string.Empty)
                .Select(s => Text(s, "JOBNAME"))
                .Where(n => !string.IsNullOrEmpty(n) && !n.StartsWith("_", StringComparison.Ordinal))
                .Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var jobs = new List<JobDescription>(names.Count);
            foreach (var name in names)
            {
                var comment = Comments(Run(ed, "_JOBCOMMENTS", name), "JOBCOMMENT").FirstOrDefault() ?? string.Empty;
                jobs.Add(new JobDescription(name, comment, [], []));
            }

            return new SgbdDescription(Path.GetFileNameWithoutExtension(ed.SgbdFileName ?? sgbd).ToUpperInvariant(), jobs);
        }
    }

    public JobDescription DescribeJob(string sgbd, string job)
    {
        lock (_lock)
        {
            var ed = Open(sgbd);
            var comment = string.Join(Environment.NewLine, Comments(Run(ed, "_JOBCOMMENTS", job), "JOBCOMMENT"));
            var args = Parameters(Run(ed, "_ARGUMENTS", job), "ARG");
            var results = Parameters(Run(ed, "_RESULTS", job), "RESULT");
            return new JobDescription(job, comment, args, results);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _ediabas?.Dispose();
            _ediabas = null;
        }
    }

    private EdiabasNet Open(string sgbd)
    {
        if (_ediabas is null)
        {
            // The interface is never connected: virtual jobs run with NoInitForVJobs, so nothing touches the bus.
            _ediabas = new EdiabasNet { EdInterfaceClass = new EdInterfaceObd { ComPort = "COM1" }, NoInitForVJobs = true };
            _ediabas.SetConfigProperty("EcuPath", _ecuPath);
        }

        try
        {
            _ediabas.ResolveSgbdFile(sgbd);
        }
        catch (EdiabasNet.EdiabasNetException ex)
        {
            throw new DiagnosticException(
                ex.ErrorCode == EdiabasNet.ErrorCodes.EDIABAS_SYS_0005
                    ? $"{sgbd} not found in {_ecuPath}"
                    : $"{sgbd}: {EdiabasNet.GetErrorDescription(ex.ErrorCode)} (group files can only be identified with the car connected)",
                ex.ErrorCode.ToString(), ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
            throw new DiagnosticException($"{sgbd} not found in {_ecuPath}", "EDIABAS_SYS_0005", ex);
        }

        return _ediabas;
    }

    private static List<Dictionary<string, EdiabasNet.ResultData>> Run(EdiabasNet ed, string vjob, string arg)
    {
        ed.ArgBinary = null;
        ed.ArgString = arg;
        ed.ResultsRequests = string.Empty;
        try
        {
            ed.ExecuteJob(vjob);
        }
        catch (EdiabasNet.EdiabasNetException)
        {
            return [];
        }

        return (ed.ResultSets ?? []).Skip(1).ToList();
    }

    private static string? Text(Dictionary<string, EdiabasNet.ResultData> set, string key) =>
        set.TryGetValue(key, out var v) ? (v.OpData as string)?.Trim() : null;

    private static IEnumerable<string> Comments(List<Dictionary<string, EdiabasNet.ResultData>> sets, string prefix) =>
        sets.SelectMany(s => s.Where(kv => kv.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(kv => kv.Key.Length).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => (kv.Value.OpData as string)?.Trim()))
            .Where(c => !string.IsNullOrEmpty(c))
            .Cast<string>();

    /// <summary>Parses _ARGUMENTS / _RESULTS sets (ARG, ARGTYPE, ARGCOMMENT0..n).</summary>
    private static List<ParameterDescription> Parameters(List<Dictionary<string, EdiabasNet.ResultData>> sets, string prefix)
    {
        var list = new List<ParameterDescription>();
        foreach (var set in sets)
        {
            var name = Text(set, prefix);
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var type = Text(set, prefix + "TYPE") ?? string.Empty;
            var comment = string.Join(" ", Comments([set], prefix + "COMMENT"));
            list.Add(new ParameterDescription(name, type, comment));
        }

        return list;
    }
}

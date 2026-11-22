using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Data;

/// <summary>
/// Describes SGBD jobs and tables from the data pack (instant, no .prg parsing). Anything the pack lacks — an SGBD
/// added to the Ecu folder after the pack was built — goes to the fallback inspector reading the files.
/// Running jobs still needs the .prg files themselves; this only replaces reading their metadata.
/// </summary>
public sealed class PackedSgbdInspector : ISgbdInspector
{
    private readonly SgbdPack _pack;
    private readonly ISgbdInspector? _fallback;

    public PackedSgbdInspector(SgbdPack pack, ISgbdInspector? fallback)
    {
        _pack = pack;
        _fallback = fallback;
    }

    public bool IsAvailable => _pack.Count > 0 || _fallback?.IsAvailable == true;

    public IReadOnlyList<SgbdFile> ListFiles()
    {
        var files = new Dictionary<string, SgbdFile>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in _pack.Index)
        {
            files[e.Name + e.IsGroup] = new SgbdFile(e.Name, e.IsGroup, _pack.Path);
        }

        foreach (var f in _fallback?.ListFiles() ?? [])
        {
            files[f.Name + f.IsGroup] = f; // a real file on disk is preferred: it can also be run
        }

        return files.Values.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public Task<SgbdDescription> DescribeAsync(string sgbd, CancellationToken cancellationToken = default)
    {
        if (_pack.Get(sgbd) is { } p)
        {
            var jobs = p.Jobs
                .Where(j => !j.Name.StartsWith('_'))
                .Select(j => new JobDescription(j.Name, j.Comments.FirstOrDefault() ?? string.Empty, [], []))
                .ToList();
            return Task.FromResult(new SgbdDescription(p.Name, jobs));
        }

        return _fallback?.DescribeAsync(sgbd, cancellationToken)
               ?? Task.FromException<SgbdDescription>(new DiagnosticException($"{sgbd} is not in the data pack", "EDIABAS_SYS_0005"));
    }

    public Task<JobDescription> DescribeJobAsync(string sgbd, string job, CancellationToken cancellationToken = default)
    {
        if (_pack.Get(sgbd)?.Job(job) is { } j)
        {
            return Task.FromResult(new JobDescription(
                j.Name,
                string.Join(Environment.NewLine, j.Comments),
                j.Arguments.Select(Param).ToList(),
                j.Results.Select(Param).ToList()));
        }

        return _fallback?.DescribeJobAsync(sgbd, job, cancellationToken)
               ?? Task.FromResult(new JobDescription(job, string.Empty, [], []));
    }

    public Task<IReadOnlyList<IReadOnlyList<string>>> ReadTableAsync(string sgbd, string table, CancellationToken cancellationToken = default)
    {
        if (_pack.Get(sgbd) is { } p)
        {
            IReadOnlyList<IReadOnlyList<string>> rows = p.Table(table).Select(r => (IReadOnlyList<string>)r).ToList();
            return Task.FromResult(rows);
        }

        return _fallback?.ReadTableAsync(sgbd, table, cancellationToken)
               ?? Task.FromResult<IReadOnlyList<IReadOnlyList<string>>>([]);
    }

    private static ParameterDescription Param(PackedParameter p) =>
        new(p.Name, p.Type, string.Join(Environment.NewLine, p.Comments));
}

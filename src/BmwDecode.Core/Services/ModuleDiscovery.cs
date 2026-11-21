using System.Globalization;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Services;

/// <summary>A control unit that answered the functional (broadcast) identification.</summary>
public sealed record DiscoveredEcu(
    int Address,
    string Abbreviation,
    string? Name,
    string? Variant,
    string? Group,
    IReadOnlyList<IdentItem> Ident,
    int? FaultCount);

/// <summary>
/// Asks the car which modules are fitted, the way ISTA does: one functional IDENT_FUNKTIONAL broadcast through the
/// main-series SGBD (E89X, E60, E65, E70) returns every ECU with its variant SGBD. FS_LESEN_FUNKTIONAL adds the
/// fault count per ECU. Only BN2000 (D-CAN) cars support this; DS2 cars are probed one by one instead.
/// </summary>
public sealed class ModuleDiscovery
{
    /// <summary>Main-series SGBDs to try when the chassis is not known yet.</summary>
    public static readonly string[] AllMainSgbds = ["E89X", "E60", "E70", "E65"];

    private readonly IDiagnosticEngine _engine;

    public ModuleDiscovery(IDiagnosticEngine engine) => _engine = engine;

    /// <summary>Returns the ECUs reported by the car, or an empty list if the car/SGBD does not support it.</summary>
    public async Task<IReadOnlyList<DiscoveredEcu>> DiscoverAsync(string mainSgbd, CancellationToken cancellationToken = default)
    {
        JobResult ident;
        try
        {
            ident = await _engine.ExecuteAsync(mainSgbd, "IDENT_FUNKTIONAL", "", cancellationToken).ConfigureAwait(false);
        }
        catch (DiagnosticException ex) when (!ex.IsInterfaceError)
        {
            return [];
        }

        var byAddress = new Dictionary<int, DiscoveredEcu>();
        foreach (var set in ident.Sets)
        {
            var address = ParseAddress(set.Text("ECU_ADR")) ?? (int?)set.Long("ID_SG_ADR");
            if (address is null || address < 0)
            {
                continue;
            }

            var status = set.Text("JOB_STATUS");
            if (status is not null && !status.Equals("OKAY", StringComparison.OrdinalIgnoreCase) && set.Text("ECU_SGBD") is null)
            {
                continue;
            }

            var item = new DiscoveredEcu(
                address.Value,
                Clean(set.Text("ECU_GROBNAME")) ?? $"0x{address:X2}",
                Clean(set.Text("ECU_NAME")),
                Clean(set.Text("ECU_SGBD"))?.ToUpperInvariant(),
                Clean(set.Text("ECU_GRUPPE"))?.ToUpperInvariant(),
                FaultParser.ParseIdent(new JobResult(mainSgbd, mainSgbd, "IDENT_FUNKTIONAL", "", ident.System, [set], TimeSpan.Zero))
                    .Where(i => i.Key.StartsWith("ID_", StringComparison.OrdinalIgnoreCase) || i.Key == "SERIENNUMMER")
                    .ToList(),
                null);
            byAddress.TryAdd(address.Value, item);
        }

        if (byAddress.Count == 0)
        {
            return [];
        }

        try
        {
            var fs = await _engine.ExecuteAsync(mainSgbd, "FS_LESEN_FUNKTIONAL", "", cancellationToken).ConfigureAwait(false);
            foreach (var set in fs.Sets)
            {
                var address = ParseAddress(set.Text("ECU_ADR")) ?? (int?)set.Long("ID_SG_ADR");
                if (address is { } a && byAddress.TryGetValue(a, out var ecu) && set.Long("F_ANZ") is { } n)
                {
                    byAddress[a] = ecu with { FaultCount = (int)n };
                }
            }
        }
        catch (DiagnosticException ex) when (!ex.IsInterfaceError)
        {
            // Fault counts are a bonus; the per-ECU FS_LESEN still runs.
        }

        return byAddress.Values.OrderBy(e => EcuPriority.Rank(e.Abbreviation, e.Address)).ThenBy(e => e.Address).ToList();
    }

    /// <summary>Tries every main-series SGBD; returns the first that answers, for cars whose chassis is unknown.</summary>
    public async Task<(string? MainSgbd, IReadOnlyList<DiscoveredEcu> Ecus)> DiscoverAnyAsync(
        IEnumerable<string> candidates, CancellationToken cancellationToken = default)
    {
        foreach (var sgbd in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var ecus = await DiscoverAsync(sgbd, cancellationToken).ConfigureAwait(false);
            if (ecus.Count > 0)
            {
                return (sgbd, ecus);
            }
        }

        return (null, []);
    }

    internal static int? ParseAddress(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            t = t[2..];
        }

        return int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : null;
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) || s.Trim() is "-" or "--" or "?" ? null : s.Trim();
}

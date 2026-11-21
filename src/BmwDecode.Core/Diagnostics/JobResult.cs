using System.Collections;
using System.Globalization;

namespace BmwDecode.Core.Diagnostics;

/// <summary>Type of a single EDIABAS result value.</summary>
public enum ResultKind
{
    Integer,
    Real,
    Text,
    Binary,
}

/// <summary>One named value returned by an EDIABAS job.</summary>
public sealed record ResultValue(string Name, ResultKind Kind, object? Raw)
{
    public string Display => Kind switch
    {
        ResultKind.Binary => Raw is byte[] bytes ? Convert.ToHexString(bytes) : string.Empty,
        ResultKind.Real => Raw is IConvertible c ? c.ToDouble(CultureInfo.InvariantCulture).ToString("0.###", CultureInfo.CurrentCulture) : string.Empty,
        _ => Convert.ToString(Raw, CultureInfo.CurrentCulture)?.Trim() ?? string.Empty,
    };

    public long? AsLong => Kind switch
    {
        ResultKind.Integer when Raw is IConvertible c => c.ToInt64(CultureInfo.InvariantCulture),
        ResultKind.Real when Raw is IConvertible c => (long)c.ToDouble(CultureInfo.InvariantCulture),
        ResultKind.Text when long.TryParse(Raw as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    public double? AsDouble => Kind switch
    {
        ResultKind.Integer or ResultKind.Real when Raw is IConvertible c => c.ToDouble(CultureInfo.InvariantCulture),
        ResultKind.Text when double.TryParse(Raw as string, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) => v,
        _ => null,
    };

    public string KindLabel => Kind switch
    {
        ResultKind.Integer => "int",
        ResultKind.Real => "real",
        ResultKind.Text => "text",
        _ => "binary",
    };
}

/// <summary>One result set ("Satz") of a job. Keys are case-insensitive.</summary>
public sealed class ResultSet : IReadOnlyDictionary<string, ResultValue>
{
    private readonly Dictionary<string, ResultValue> _values;
    private readonly List<string> _order;

    public ResultSet(IEnumerable<ResultValue> values)
    {
        _values = new Dictionary<string, ResultValue>(StringComparer.OrdinalIgnoreCase);
        _order = [];
        foreach (var value in values)
        {
            if (_values.TryAdd(value.Name, value))
            {
                _order.Add(value.Name);
            }
        }
    }

    /// <summary>Values in the order EDIABAS produced them.</summary>
    public IEnumerable<ResultValue> Ordered => _order.Select(k => _values[k]);

    public string? Text(string name) => _values.TryGetValue(name, out var v) ? v.Display : null;
    public long? Long(string name) => _values.TryGetValue(name, out var v) ? v.AsLong : null;
    public double? Double(string name) => _values.TryGetValue(name, out var v) ? v.AsDouble : null;

    public ResultValue this[string key] => _values[key];
    public IEnumerable<string> Keys => _order;
    public IEnumerable<ResultValue> Values => Ordered;
    public int Count => _values.Count;
    public bool ContainsKey(string key) => _values.ContainsKey(key);
    public bool TryGetValue(string key, out ResultValue value) => _values.TryGetValue(key, out value!);
    public IEnumerator<KeyValuePair<string, ResultValue>> GetEnumerator() =>
        _order.Select(k => new KeyValuePair<string, ResultValue>(k, _values[k])).GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

/// <summary>Complete outcome of an EDIABAS job execution.</summary>
public sealed class JobResult
{
    public JobResult(string sgbd, string variant, string job, string args, ResultSet system, IReadOnlyList<ResultSet> sets, TimeSpan duration)
    {
        Sgbd = sgbd;
        Variant = variant;
        Job = job;
        Args = args;
        System = system;
        Sets = sets;
        Duration = duration;
    }

    /// <summary>File name that was requested (group or variant).</summary>
    public string Sgbd { get; }

    /// <summary>Variant SGBD that actually ran (e.g. D60M57A0 for group D_MOTOR).</summary>
    public string Variant { get; }

    public string Job { get; }
    public string Args { get; }

    /// <summary>EDIABAS system results (set 0): VARIANTE, UBATTCURRENT, IGNITIONCURRENT...</summary>
    public ResultSet System { get; }

    /// <summary>Job result sets (EDIABAS sets 1..n).</summary>
    public IReadOnlyList<ResultSet> Sets { get; }

    public TimeSpan Duration { get; }

    /// <summary>JOB_STATUS of the job; EDIABAS puts it in the last set for multi-set jobs.</summary>
    public string? JobStatus =>
        Sets.Select(s => s.Text("JOB_STATUS")).LastOrDefault(s => !string.IsNullOrEmpty(s));

    public bool IsOkay => Sets.Any(s => string.Equals(s.Text("JOB_STATUS"), "OKAY", StringComparison.OrdinalIgnoreCase));

    /// <summary>Battery (terminal 30) present on the OBD socket as seen by the interface; null if unknown.</summary>
    /// <remarks>EDIABAS reports UBATTCURRENT as a flag: 1 = on, 0 = off, -1 = not available.</remarks>
    public bool? BatteryOn => System.Long("UBATTCURRENT") switch
    {
        null or < 0 => null,
        var v => v > 0,
    };

    /// <summary>Ignition (terminal 15) state reported by the interface, if supported.</summary>
    public bool? IgnitionOn => System.Long("IGNITIONCURRENT") switch
    {
        null or < 0 => null,
        var v => v > 0,
    };
}

/// <summary>Error raised by a diagnostic engine.</summary>
public class DiagnosticException : Exception
{
    public DiagnosticException(string message, string? errorCode = null, Exception? inner = null)
        : base(message, inner)
    {
        ErrorCode = errorCode;
    }

    /// <summary>EDIABAS error code name such as IFH_0009 (no response from control unit).</summary>
    public string? ErrorCode { get; }

    /// <summary>True if the ECU simply did not answer (not fitted, asleep, wrong bus).</summary>
    public bool IsNoResponse => HasCode("IFH_0009", "SYS_0012", "NO_RESPONSE");

    /// <summary>True if the cable/interface itself failed (wrong COM port, unplugged, no 12 V on OBD pin 16).</summary>
    public bool IsInterfaceError => HasCode("IFH_0002", "IFH_0003", "IFH_0011", "IFH_0018");

    private bool HasCode(params string[] codes) =>
        ErrorCode is not null && codes.Any(c => ErrorCode.Contains(c, StringComparison.OrdinalIgnoreCase));
}

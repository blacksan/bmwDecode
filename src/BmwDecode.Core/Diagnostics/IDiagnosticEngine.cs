namespace BmwDecode.Core.Diagnostics;

/// <summary>How the PC talks to the car.</summary>
public enum InterfaceKind
{
    /// <summary>K+DCAN USB cable through its virtual COM port (INPA style, e.g. COM1).</summary>
    KDcanSerial,

    /// <summary>K+DCAN USB cable through the FTDI D2XX driver (ftd2xx.dll), no COM port needed.</summary>
    KDcanFtdi,

    /// <summary>BMW ENET (Ethernet) cable – F/G/I series, some late E-series.</summary>
    Enet,

    /// <summary>Built-in simulated E84 – no car or cable required.</summary>
    Demo,
}

/// <summary>Everything needed to open a diagnostic session.</summary>
public sealed record ConnectionSettings
{
    public InterfaceKind Kind { get; init; } = InterfaceKind.KDcanSerial;

    /// <summary>COM port (COM1), FTDI device (FTDI0) or ENET host ("auto" or an IP address).</summary>
    public string Port { get; init; } = "COM1";

    /// <summary>EDIABAS ECU folder holding the .prg/.grp SGBD files, e.g. C:\EDIABAS\Ecu.</summary>
    public string EcuPath { get; init; } = string.Empty;

    /// <summary>Write an EDIABAS interface trace (ifh.trc) for troubleshooting.</summary>
    public bool Trace { get; init; }

    public string? TracePath { get; init; }
}

/// <summary>Executes EDIABAS jobs against a vehicle (real or simulated).</summary>
/// <remarks>Implementations serialise access: a diagnostic bus can only run one job at a time.</remarks>
public interface IDiagnosticEngine : IAsyncDisposable
{
    string Name { get; }
    bool IsConnected { get; }
    bool IsSimulation { get; }

    /// <summary>Raised for every job and connection event, for the session log.</summary>
    event EventHandler<DiagnosticLogEntry>? Log;

    Task ConnectAsync(ConnectionSettings settings, CancellationToken cancellationToken = default);

    Task DisconnectAsync();

    /// <summary>
    /// Resolves a group file (e.g. D_MOTOR) to the variant SGBD that matches the fitted ECU (e.g. D60M57A0).
    /// Throws <see cref="DiagnosticException"/> with <see cref="DiagnosticException.IsNoResponse"/> when the ECU does not answer.
    /// </summary>
    Task<string> ResolveVariantAsync(string sgbd, CancellationToken cancellationToken = default);

    /// <summary>Runs one job. <paramref name="args"/> uses the EDIABAS ';'-separated argument syntax.</summary>
    Task<JobResult> ExecuteAsync(string sgbd, string job, string args = "", CancellationToken cancellationToken = default);
}

/// <summary>Reads SGBD metadata (jobs, arguments, results, tables) without talking to the car.</summary>
public interface ISgbdInspector
{
    /// <summary>True when an EDIABAS ECU folder with SGBD files is configured.</summary>
    bool IsAvailable { get; }

    IReadOnlyList<SgbdFile> ListFiles();

    Task<SgbdDescription> DescribeAsync(string sgbd, CancellationToken cancellationToken = default);

    Task<JobDescription> DescribeJobAsync(string sgbd, string job, CancellationToken cancellationToken = default);
}

public sealed record SgbdFile(string Name, bool IsGroup, string FullPath);

public sealed record SgbdDescription(string Name, IReadOnlyList<JobDescription> Jobs);

public sealed record ParameterDescription(string Name, string Type, string Comment);

public sealed record JobDescription(
    string Name,
    string Comment,
    IReadOnlyList<ParameterDescription> Arguments,
    IReadOnlyList<ParameterDescription> Results)
{
    public bool HasDetails => Arguments.Count > 0 || Results.Count > 0;

    /// <summary>
    /// Jobs that write to the ECU (coding, flashing, actuator tests, resets). The UI asks for
    /// confirmation before running them.
    /// </summary>
    public bool IsPotentiallyDestructive => JobSafety.IsPotentiallyDestructive(Name);
}

public static class JobSafety
{
    private static readonly string[] DangerousFragments =
    [
        "SCHREIBEN", "FLASH", "LOESCHEN", "STEUERN", "PROGRAMM", "RESET", "C_C_", "C_FA_", "C_FG_AUFTRAG",
        "C_FG_SCHREIBEN", "AIF_SCHREIBEN", "ABGLEICH", "LERNWERTE", "ADAP", "SET_", "WRITE", "AUTHENTISIERUNG",
        "STEUERGERAETE_RESET", "SPEICHER_SCHREIBEN", "_COD", "SLEEP_MODE", "ENERGIESPARMODE", "DIAGNOSE_MODE",
    ];

    public static bool IsPotentiallyDestructive(string jobName)
    {
        if (string.IsNullOrWhiteSpace(jobName) || jobName.StartsWith("_JOB", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var upper = jobName.ToUpperInvariant();

        // Read-only jobs by naming convention, unless they also write or clear something.
        var looksReadOnly = upper.StartsWith("STATUS_", StringComparison.Ordinal) ||
                            upper.EndsWith("_LESEN", StringComparison.Ordinal) ||
                            upper.Contains("_LESEN_", StringComparison.Ordinal);
        if (looksReadOnly)
        {
            return upper.Contains("SCHREIBEN", StringComparison.Ordinal) || upper.Contains("LOESCHEN", StringComparison.Ordinal);
        }

        return DangerousFragments.Any(upper.Contains);
    }
}

public enum LogLevel
{
    Debug,
    Info,
    Warning,
    Error,
}

public sealed record DiagnosticLogEntry(DateTimeOffset Time, LogLevel Level, string Message)
{
    public static DiagnosticLogEntry Now(LogLevel level, string message) => new(DateTimeOffset.Now, level, message);
}

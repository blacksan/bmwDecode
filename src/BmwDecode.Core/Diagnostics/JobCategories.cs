namespace BmwDecode.Core.Diagnostics;

/// <summary>Groups SGBD jobs by purpose so each module can present "what you can do" in a sensible order.</summary>
public static class JobCategories
{
    public const string Faults = "Fault memory";
    public const string Status = "Status / live values";
    public const string Ident = "Identification";
    public const string Actuators = "Actuator tests";
    public const string Adaptations = "Adaptations, calibration & resets";
    public const string Coding = "Coding";
    public const string Programming = "Programming / flash";
    public const string Session = "Diagnostic session";
    public const string Other = "Other";

    public static readonly string[] Order = [Faults, Status, Ident, Actuators, Adaptations, Coding, Programming, Session, Other];

    public static string Of(string job)
    {
        var j = job.ToUpperInvariant();
        if (j.StartsWith("FS_", StringComparison.Ordinal) || j.StartsWith("IS_", StringComparison.Ordinal) ||
            j.StartsWith("HS_", StringComparison.Ordinal) || j.Contains("FEHLER", StringComparison.Ordinal))
        {
            return Faults;
        }

        if (j.StartsWith("C_", StringComparison.Ordinal) || j.Contains("_COD", StringComparison.Ordinal) ||
            j.Contains("CODIER", StringComparison.Ordinal) || j.StartsWith("CODING", StringComparison.Ordinal))
        {
            return Coding;
        }

        if (j.StartsWith("STEUERN", StringComparison.Ordinal) || j.Contains("_STEUERN", StringComparison.Ordinal) ||
            j.StartsWith("ANSTEUERN", StringComparison.Ordinal) || j.StartsWith("START_", StringComparison.Ordinal) ||
            j.StartsWith("STOP_", StringComparison.Ordinal))
        {
            return Actuators;
        }

        if (j.Contains("ABGLEICH", StringComparison.Ordinal) || j.Contains("ADAP", StringComparison.Ordinal) ||
            j.Contains("LERNWERTE", StringComparison.Ordinal) || j.Contains("RESET", StringComparison.Ordinal) ||
            j.Contains("INITIALISIER", StringComparison.Ordinal) && j != "INITIALISIERUNG" || j.Contains("CBS", StringComparison.Ordinal) ||
            j.Contains("KALIBR", StringComparison.Ordinal) || j.Contains("EINLERNEN", StringComparison.Ordinal) ||
            j.Contains("NORMIER", StringComparison.Ordinal) || j.Contains("REGISTRIEREN", StringComparison.Ordinal))
        {
            return Adaptations;
        }

        if (j.Contains("FLASH", StringComparison.Ordinal) || j.Contains("PROG", StringComparison.Ordinal) ||
            j.StartsWith("AIF_", StringComparison.Ordinal) || j.Contains("AUTHENTISIERUNG", StringComparison.Ordinal) ||
            j.Contains("SIGNATUR", StringComparison.Ordinal) || j.Contains("SPEICHER_", StringComparison.Ordinal))
        {
            return Programming;
        }

        if (j.StartsWith("STATUS", StringComparison.Ordinal) || j.StartsWith("MW_", StringComparison.Ordinal) ||
            j.Contains("MESSWERT", StringComparison.Ordinal) || j.StartsWith("STAT_", StringComparison.Ordinal))
        {
            return Status;
        }

        if (j.Contains("IDENT", StringComparison.Ordinal) || j.Contains("SERIENNUMMER", StringComparison.Ordinal) ||
            j.Contains("HW_NR", StringComparison.Ordinal) || j.Contains("ZIF", StringComparison.Ordinal) ||
            j.Contains("VERSION", StringComparison.Ordinal) || j == "INFO" || j.Contains("PRUEFSTEMPEL", StringComparison.Ordinal) ||
            j.Contains("REFERENZ", StringComparison.Ordinal))
        {
            return Ident;
        }

        if (j.StartsWith("DIAGNOSE", StringComparison.Ordinal) || j == "INITIALISIERUNG" || j.Contains("SLEEP", StringComparison.Ordinal) ||
            j.Contains("ENERGIESPAR", StringComparison.Ordinal) || j.Contains("DATENVERKEHR", StringComparison.Ordinal) ||
            j.Contains("PROTOKOLL", StringComparison.Ordinal))
        {
            return Session;
        }

        return Other;
    }

    /// <summary>Coding jobs that only read (safe to run and back up).</summary>
    public static bool IsCodingRead(string job) =>
        Of(job) == Coding && job.EndsWith("_LESEN", StringComparison.OrdinalIgnoreCase);
}

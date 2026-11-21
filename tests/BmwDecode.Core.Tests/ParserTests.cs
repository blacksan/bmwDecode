using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Tests;

public class ParserTests
{
    private static ResultSet Set(params (string Name, object Value)[] values) =>
        new(values.Select(v => v.Value switch
        {
            string s => new ResultValue(v.Name, ResultKind.Text, s),
            double d => new ResultValue(v.Name, ResultKind.Real, d),
            byte[] b => new ResultValue(v.Name, ResultKind.Binary, b),
            _ => new ResultValue(v.Name, ResultKind.Integer, Convert.ToInt64(v.Value)),
        }));

    private static JobResult Job(string job, params ResultSet[] sets) =>
        new("D_MOTOR", "D60M57A0", job, "", Set(("VARIANTE", "D60M57A0"), ("UBATTCURRENT", 1L), ("IGNITIONCURRENT", 1L)), sets, TimeSpan.Zero);

    [Fact]
    public void Faults_are_read_from_every_set_with_a_location_number()
    {
        var result = Job("FS_LESEN",
            Set(("F_ORT_NR", 0x4B93L), ("F_ORT_TEXT", " EWS manipulation "), ("F_VORHANDEN_NR", 0x23L), ("F_WARNUNG_NR", 0x30L), ("F_HFK", 4L), ("_TEL_ANTWORT", new byte[] { 1, 2 })),
            Set(("F_ORT_NR", 0x2AAFL), ("F_ORT_TEXT", "Rail pressure"), ("F_VORHANDEN_NR", 0x21L), ("F_WARNUNG_NR", 0x31L)),
            Set(("JOB_STATUS", "OKAY")));

        var faults = FaultParser.ParseFaults(result);

        Assert.Equal(2, faults.Count);
        Assert.Equal("4B93", faults[0].CodeHex);
        Assert.Equal("EWS manipulation", faults[0].Text);
        Assert.True(faults[0].IsPresent);
        Assert.False(faults[0].WarningLampOn);
        Assert.Equal(4, faults[0].Frequency);
        Assert.DoesNotContain(faults[0].Raw, kv => kv.Key == "_TEL_ANTWORT");
        Assert.False(faults[1].IsPresent);
        Assert.True(faults[1].WarningLampOn);
        Assert.True(result.IsOkay);
        Assert.Equal("OKAY", result.JobStatus);
        Assert.True(result.BatteryOn);
        Assert.True(result.IgnitionOn);
    }

    [Fact]
    public void Fault_type_0x22_debouncing_counts_as_present()
    {
        // "Fehler momentan vorhanden, aber noch nicht gespeichert" contains "nicht" but means *present*.
        var result = Job("FS_LESEN", Set(("F_ORT_NR", 1L), ("F_VORHANDEN_NR", 0x22L),
            ("F_VORHANDEN_TEXT", "Fehler momentan vorhanden, aber noch nicht gespeichert (Entprellphase)")));

        Assert.True(FaultParser.ParseFaults(result)[0].IsPresent);
    }

    [Fact]
    public void Text_fallback_handles_real_SGBD_wording()
    {
        Assert.False(FaultParser.InterpretPresent("Fehler momentan nicht vorhanden, aber bereits gespeichert"));
        Assert.True(FaultParser.InterpretPresent("Fehler momentan vorhanden und bereits gespeichert"));
        Assert.True(FaultParser.InterpretPresent("Fehler momentan vorhanden, aber noch nicht gespeichert (Entprellphase)"));
        Assert.Null(FaultParser.InterpretPresent(null));
        Assert.False(FaultParser.InterpretWarning("Fehler würde kein Aufleuchten einer Warnlampe verursachen"));
        Assert.True(FaultParser.InterpretWarning("Fehler würde das Aufleuchten einer Warnlampe verursachen"));
    }

    [Fact]
    public void Fault_detail_extracts_environment_conditions_in_order()
    {
        var result = Job("FS_LESEN_DETAIL", Set(
            ("F_ORT_NR", 0x4B93L), ("F_UW_KM", 152_310L), ("F_HFK", 14L), ("F_LZ", 40L),
            ("F_UW2_TEXT", "Motordrehzahl"), ("F_UW2_WERT", 812.0), ("F_UW2_EINH", "1/min"),
            ("F_UW1_TEXT", "Batteriespannung"), ("F_UW1_WERT", 12.3), ("F_UW1_EINH", "V"),
            ("F_UW3_TEXT", "Status"), ("F_UW3_WERT", "aktiv"), ("F_UW3_EINH", "-")));

        var detail = FaultParser.ParseDetail(0x4B93, result);

        Assert.Equal(152_310, detail.Mileage);
        Assert.Equal(14, detail.Frequency);
        Assert.Equal(new[] { "Batteriespannung", "Motordrehzahl", "Status" }, detail.Conditions.Select(c => c.Name).ToArray());
        Assert.Equal("V", detail.Conditions[0].Unit);
        Assert.Null(detail.Conditions[2].Unit);
    }

    [Fact]
    public void Ident_uses_friendly_labels_and_hides_telegrams()
    {
        var items = FaultParser.ParseIdent(Job("IDENT", Set(
            ("ID_BMW_NR", "7811571"), ("ID_SG_ADR", 0x12L), ("ID_SONDERWERT", "x"), ("_TEL_AUFTRAG", new byte[] { 0x12 }), ("JOB_STATUS", "OKAY"))));

        Assert.Equal(3, items.Count);
        Assert.Equal("BMW part number", items[0].Label);
        Assert.Equal("0x12", items[1].Value);
        Assert.Equal("Sonderwert", items[2].Label);
    }

    [Fact]
    public void Live_values_pair_WERT_with_EINH()
    {
        var values = FaultParser.ParseLive(Job("STATUS_MOTORTEMPERATUR", Set(
            ("STAT_ID_SG_ADR", 0x12L), ("STAT_MOTORTEMPERATUR_WERT", 87.5), ("STAT_MOTORTEMPERATUR_EINH", "°C"), ("STAT_INFO", "ok"), ("JOB_STATUS", "OKAY"))));

        Assert.Equal(2, values.Count);
        Assert.Equal("Motortemperatur", values[0].Label);
        Assert.Equal("°C", values[0].Unit);
        Assert.Equal(87.5, values[0].Numeric);
        Assert.Equal("Info", values[1].Label);
        Assert.Null(values[1].Numeric);
    }

    [Fact]
    public void Diagnostic_errors_are_classified()
    {
        Assert.True(new DiagnosticException("x", "EDIABAS_IFH_0009").IsNoResponse);
        Assert.True(new DiagnosticException("x", "EDIABAS_IFH_0018").IsInterfaceError);
        Assert.False(new DiagnosticException("x", "EDIABAS_IFH_0018").IsNoResponse);
        Assert.False(new DiagnosticException("x").IsNoResponse);
    }

    [Fact]
    public void Writing_jobs_are_flagged_as_destructive()
    {
        Assert.True(JobSafety.IsPotentiallyDestructive("FS_LOESCHEN"));
        Assert.True(JobSafety.IsPotentiallyDestructive("C_FA_SCHREIBEN"));
        Assert.True(JobSafety.IsPotentiallyDestructive("STEUERN_E_LUEFTER"));
        Assert.True(JobSafety.IsPotentiallyDestructive("FLASH_LOESCHEN"));
        Assert.False(JobSafety.IsPotentiallyDestructive("FS_LESEN"));
        Assert.False(JobSafety.IsPotentiallyDestructive("STATUS_MOTORDREHZAHL"));
        Assert.False(JobSafety.IsPotentiallyDestructive("IDENT"));
        Assert.False(JobSafety.IsPotentiallyDestructive("_JOBS"));
    }
}

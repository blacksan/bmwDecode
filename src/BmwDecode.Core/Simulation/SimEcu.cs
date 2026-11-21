using System.Globalization;
using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Simulation;

internal sealed record SimFault(long Code, string Text, string Symptom, bool Present, bool Lamp, int Count, long Km);

internal sealed record SimLive(string Job, string Name, string Unit, double Min, double Max);

/// <summary>One simulated control unit.</summary>
internal sealed class SimEcu
{
    private static readonly ParameterDescription[] s_faultResults =
    [
        new("F_ORT_NR", "long", "Fault location number"),
        new("F_ORT_TEXT", "string", "Fault location text"),
        new("F_SYMPTOM_TEXT", "string", "Fault symptom"),
        new("F_VORHANDEN_NR", "int", "Present flag"),
        new("F_VORHANDEN_TEXT", "string", "Present flag text"),
        new("F_WARNUNG_NR", "int", "Warning lamp flag"),
        new("F_WARNUNG_TEXT", "string", "Warning lamp text"),
        new("F_HFK", "int", "Frequency counter"),
        new("JOB_STATUS", "string", "OKAY if no error"),
    ];

    public SimEcu(string group, string variant, string partNumber, string supplier, IEnumerable<SimFault>? faults = null,
        IEnumerable<SimFault>? info = null, IEnumerable<SimLive>? live = null, params string[] aliases)
    {
        Group = group;
        Variant = variant;
        PartNumber = partNumber;
        Supplier = supplier;
        Faults = faults?.ToList() ?? [];
        Info = info?.ToList() ?? [];
        Live = live?.ToList() ?? [];
        Aliases = aliases;

        var jobs = new List<JobDescription>
        {
            new("IDENT", "Identification data", [], [new("ID_BMW_NR", "string", "BMW part number"), new("ID_HW_NR", "string", "Hardware number"), new("ID_DATUM", "string", "Manufacturing date"), new("ID_LIEF_TEXT", "string", "Supplier"), new("JOB_STATUS", "string", "OKAY if no error")]),
            new("FS_LESEN", "Read fault memory (all faults)", [], s_faultResults),
            new("FS_LESEN_DETAIL", "Read fault memory details for one fault", [new("F_CODE", "int", "Selected fault code")], [new("F_UW_KM", "long", "Mileage"), new("F_UW_ANZ", "int", "Number of conditions"), new("JOB_STATUS", "string", "OKAY if no error")]),
            new("FS_LOESCHEN", "Clear fault memory", [], [new("JOB_STATUS", "string", "OKAY if no error")]),
            new("IS_LESEN", "Read info (shadow) memory", [], s_faultResults),
            new("IS_LOESCHEN", "Clear info memory", [], [new("JOB_STATUS", "string", "OKAY if no error")]),
        };
        foreach (var job in Live.GroupBy(l => l.Job))
        {
            jobs.Add(new JobDescription(job.Key, $"Read {string.Join(", ", job.Select(j => j.Name.ToLowerInvariant()))}", [],
                job.SelectMany(j => new ParameterDescription[] { new($"STAT_{j.Name}_WERT", "real", j.Name), new($"STAT_{j.Name}_EINH", "string", "Unit") })
                    .Append(new("JOB_STATUS", "string", "OKAY if no error")).ToList()));
        }

        Jobs = jobs.ToDictionary(j => j.Name, StringComparer.OrdinalIgnoreCase);
    }

    public string Group { get; }
    public string Variant { get; }
    public string PartNumber { get; }
    public string Supplier { get; }
    public List<SimFault> Faults { get; }
    public List<SimFault> Info { get; }
    public List<SimLive> Live { get; }
    public string[] Aliases { get; }
    public Dictionary<string, JobDescription> Jobs { get; }

    public IReadOnlyList<ResultSet> Run(string job, string args, Random random)
    {
        switch (job)
        {
            case "IDENT":
                return [SimulatedEngine.Set(
                    ("ID_BMW_NR", PartNumber), ("ID_HW_NR", "01"), ("ID_COD_INDEX", 12L), ("ID_DIAG_INDEX", 7L), ("ID_VAR_INDEX", 1L),
                    ("ID_DATUM", "14.09.2009"), ("ID_LIEF_TEXT", Supplier), ("ID_SW_NR_FSV", "2E"), ("ID_SW_NR_OSV", "1A"), ("JOB_STATUS", "OKAY"))];
            case "FS_LESEN":
                return FaultSets(Faults);
            case "IS_LESEN":
                return FaultSets(Info);
            case "FS_LOESCHEN":
                Faults.RemoveAll(f => !f.Present); // active faults come straight back, like a real car
                return [SimulatedEngine.Set(("JOB_STATUS", "OKAY"))];
            case "IS_LOESCHEN":
                Info.Clear();
                return [SimulatedEngine.Set(("JOB_STATUS", "OKAY"))];
            case "FS_LESEN_DETAIL":
                return Detail(args);
            case "STATUS_FAHRGESTELLNUMMER" when Variant == "CAS":
                return [SimulatedEngine.Set(("FGNUMMER", SimulatedEngine.DemoVin), ("JOB_STATUS", "OKAY"))];
            case "C_FG_LESEN" when Variant is "CAS" or "JBBF70":
                return [SimulatedEngine.Set(("FG_NR", SimulatedEngine.DemoVin), ("JOB_STATUS", "OKAY"))];
            case "C_FA_LESEN" when Variant == "CAS":
                return [SimulatedEngine.Set(("FAHRZEUGAUFTRAG", "E84_#0909%0730&DEMO*"), ("JOB_STATUS", "OKAY"))];
        }

        var live = Live.Where(l => l.Job.Equals(job, StringComparison.OrdinalIgnoreCase)).ToList();
        if (live.Count > 0)
        {
            var values = new List<(string, object)>();
            foreach (var l in live)
            {
                var span = l.Max - l.Min;
                var v = Math.Round(l.Min + span * (0.45 + 0.1 * random.NextDouble()), 2);
                values.Add(($"STAT_{l.Name}_WERT", v));
                values.Add(($"STAT_{l.Name}_EINH", l.Unit));
            }

            values.Add(("JOB_STATUS", "OKAY"));
            return [SimulatedEngine.Set([.. values])];
        }

        return [SimulatedEngine.Set(("JOB_STATUS", "ERROR_ECU_SERVICE_NOT_SUPPORTED"))];
    }

    private static IReadOnlyList<ResultSet> FaultSets(List<SimFault> faults)
    {
        var sets = faults.Select(f => SimulatedEngine.Set(
            ("F_ORT_NR", f.Code), ("F_ORT_TEXT", f.Text), ("F_SYMPTOM_TEXT", f.Symptom),
            ("F_VORHANDEN_NR", f.Present ? 0x23L : 0x21L),
            ("F_VORHANDEN_TEXT", f.Present ? "Fehler momentan vorhanden und bereits gespeichert" : "Fehler momentan nicht vorhanden, aber bereits gespeichert"),
            ("F_WARNUNG_NR", f.Lamp ? 0x31L : 0x30L),
            ("F_WARNUNG_TEXT", f.Lamp ? "Fehler würde das Aufleuchten einer Warnlampe verursachen" : "Fehler würde kein Aufleuchten einer Warnlampe verursachen"),
            ("F_HFK", (long)f.Count), ("JOB_STATUS", "OKAY"))).ToList();
        sets.Add(SimulatedEngine.Set(("JOB_STATUS", "OKAY")));
        return sets;
    }

    private IReadOnlyList<ResultSet> Detail(string args)
    {
        var arg = args.Split(';')[0].Trim();
        long code = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.Parse(arg[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
            : long.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out var d) ? d : -1;
        var f = Faults.Concat(Info).FirstOrDefault(x => x.Code == code);
        if (f is null)
        {
            return [SimulatedEngine.Set(("JOB_STATUS", "ERROR_ARGUMENT"))];
        }

        return [SimulatedEngine.Set(
            ("F_ORT_NR", f.Code), ("F_ORT_TEXT", f.Text), ("F_HFK", (long)f.Count), ("F_LZ", 40L), ("F_UW_KM", f.Km), ("F_UW_ANZ", 3L),
            ("F_UW1_TEXT", "Batteriespannung"), ("F_UW1_WERT", 12.3), ("F_UW1_EINH", "V"),
            ("F_UW2_TEXT", "Motordrehzahl"), ("F_UW2_WERT", 812.0), ("F_UW2_EINH", "1/min"),
            ("F_UW3_TEXT", "Kühlmitteltemperatur"), ("F_UW3_WERT", 87.0), ("F_UW3_EINH", "°C"),
            ("JOB_STATUS", "OKAY"))];
    }

    /// <summary>The demo car. Mirrors a typical E84 M57 swap with an E90 CAS and RWD conversion.</summary>
    public static IEnumerable<SimEcu> CreateE84() =>
    [
        new("D_ZGM", "JBBF70", "9 147 128", "Lear",
            info: [new(0x93C4, "Wake-up line: bus communication interrupted", "Signal missing", false, false, 2, 151_200)]),
        new("D_SZL", "SZL_87", "9 123 041", "Kostal"),
        new("D_SIM", "ACSM3", "9 184 432", "Autoliv"),
        new("D_MOTOR", "D60M57A0", "7 811 571", "Bosch",
            faults:
            [
                new(0x4B93, "EWS: no valid start value received from CAS (DDE not matched to CAS)", "Signal implausible", true, false, 14, 152_310),
                new(0x4A26, "Charge air pressure sensor: plausibility", "Signal above threshold", false, true, 3, 149_880),
                new(0x4BB0, "Transfer case (VGSG): message missing", "No signal", true, false, 41, 152_305),
            ],
            live:
            [
                new("STATUS_MOTORDREHZAHL", "MOTORDREHZAHL", "1/min", 700, 900),
                new("STATUS_MOTORTEMPERATUR", "MOTORTEMPERATUR", "°C", 82, 92),
                new("STATUS_UBATT", "UBATT", "V", 13.6, 14.4),
                new("STATUS_LMM_MASSE", "LMM_MASSE", "kg/h", 18, 26),
                new("STATUS_AN_LUFTTEMPERATUR", "AN_LUFTTEMPERATUR", "°C", 20, 34),
                new("STATUS_RAILDRUCK", "RAILDRUCK", "bar", 250, 290),
                new("STATUS_LADEDRUCK", "LADEDRUCK", "hPa", 990, 1030),
            ],
            aliases: ["D_0012"]),
        new("D_EGS", "GS19D", "7 588 463", "ZF",
            faults: [new(0x5089, "Transfer case: CAN message missing", "No signal", true, false, 37, 152_305)],
            live:
            [
                new("STATUS_GETRIEBETEMPERATUR", "GETRIEBETEMPERATUR", "°C", 60, 75),
                new("STATUS_ABTRIEBSDREHZAHL", "ABTRIEBSDREHZAHL", "1/min", 0, 0),
            ]),
        new("D_DSC", "DSC_87", "6 789 300", "Continental Teves",
            faults:
            [
                new(0x5E20, "Steering angle sensor: not initialised", "Signal missing", true, true, 9, 152_300),
                new(0x5E5C, "Engine torque signal from DDE implausible", "Signal implausible", false, true, 2, 152_290),
            ],
            live: [new("STATUS_RADGESCHWINDIGKEIT", "RADGESCHWINDIGKEIT_VL", "km/h", 0, 0)]),
        new("D_EKP", "EKPM60_2", "7 276 076", "Continental"),
        new("D_CAS", "CAS", "9 147 217", "Siemens",
            faults: [new(0xA0B4, "Key 2: transponder not recognised", "Signal missing", false, false, 1, 150_010)]),
        new("D_KOMBI", "KOMB87", "9 187 045", "Visteon",
            faults: [new(0x9306, "Message (engine coolant temperature) missing", "No signal", false, false, 5, 151_990)]),
        new("D_KBM", "FRM_70", "9 216 432", "Lear"),
        new("D_KLIMA", "IHKA87", "9 199 260", "Behr"),
        new("D_PDC", "PDC_E87", "9 127 642", "Valeo"),
        new("D_RLS", "RLS_87", "9 135 160", "Hella"),
        new("D_FZD", "FZD_87", "9 171 520", "Lear"),
        new("D_MMI", "MASK60", "9 189 455", "Harman"),
    ];
}

namespace BmwDecode.Core.Catalog;

/// <summary>English names for BMW control unit abbreviations.</summary>
public static class EcuNames
{
    private static readonly Dictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["JBBF"] = "Junction box electronics (central gateway)",
        ["ZGM"] = "Central gateway module",
        ["MRS"] = "Airbag / multiple restraint system",
        ["ACSM"] = "Airbag / crash safety module",
        ["SZL"] = "Steering column switch cluster",
        ["TRSVC"] = "Top / rear / side view camera",
        ["SME"] = "Battery management electronics",
        ["DME/DDE"] = "Engine electronics (DME petrol / DDE diesel)",
        ["DME"] = "Digital motor electronics (petrol)",
        ["DDE"] = "Digital diesel electronics",
        ["DME2"] = "Engine electronics, second bank",
        ["LIM"] = "Charging interface module",
        ["KLE"] = "Convenience charging electronics",
        ["AFS"] = "Active front steering",
        ["EKP"] = "Electric fuel pump control",
        ["EGS"] = "Automatic transmission control",
        ["VGSG"] = "Transfer case control (xDrive)",
        ["AE"] = "Electric drive electronics",
        ["SMES1"] = "Power electronics 1",
        ["SMES2"] = "Power electronics 2",
        ["LDM"] = "Longitudinal dynamics management",
        ["RDC"] = "Tyre pressure control",
        ["ACC"] = "Active cruise control",
        ["CVM/CTM"] = "Convertible top module",
        ["RSE"] = "Rear seat entertainment",
        ["PGS"] = "Comfort access (passive go)",
        ["DSC"] = "Dynamic stability control (ABS/DSC)",
        ["EMF"] = "Electromechanical parking brake",
        ["VSG"] = "Vehicle sound generator",
        ["EPS"] = "Electric power steering",
        ["TEL/MULF"] = "Telephone / Bluetooth (MULF)",
        ["TEL"] = "Telephone",
        ["AMP"] = "Audio amplifier",
        ["EHC"] = "Air suspension",
        ["EDC"] = "Electronic damper control",
        ["NAV"] = "Navigation",
        ["CDC"] = "CD changer",
        ["HUD"] = "Head-up display",
        ["ASK"] = "Audio system controller",
        ["CAS"] = "Car access system (keys, immobiliser, start)",
        ["DWA"] = "Anti-theft alarm",
        ["SHD/MDS"] = "Sunroof / panorama roof",
        ["SHD"] = "Sunroof",
        ["ANTTU"] = "Antenna tuner",
        ["VIDEO"] = "Video module",
        ["SINE"] = "Alarm siren and tilt sensor",
        ["RADIO"] = "Radio",
        ["ISPB"] = "Integrated supply module",
        ["FZD"] = "Roof function centre",
        ["RLS"] = "Rain / light sensor",
        ["DAB"] = "Digital radio tuner",
        ["GWS"] = "Gear selector switch",
        ["FLA"] = "High-beam assistant",
        ["KOMBI"] = "Instrument cluster",
        ["FBI"] = "Emergency call (eCall)",
        ["MOSTGW"] = "MOST gateway",
        ["MASK/CCC"] = "Audio / infotainment head unit (M-ASK, CCC, CIC)",
        ["PDC"] = "Park distance control",
        ["ZBE"] = "iDrive controller",
        ["FAH"] = "Seat heating, driver",
        ["BFH"] = "Seat heating, passenger",
        ["HKL"] = "Tailgate lift",
        ["FAS"] = "Seat module, driver",
        ["BFS"] = "Seat module, passenger",
        ["LM"] = "Light module",
        ["AHM"] = "Trailer module",
        ["FRM"] = "Footwell module (lights, windows)",
        ["CID"] = "Central information display",
        ["CIDF"] = "Rear display",
        ["CIDF2"] = "Rear display 2",
        ["KLIMA"] = "Climate control (IHKA)",
        ["IHKA"] = "Climate control (IHKA)",
        ["CDCDSP"] = "CD changer / DSP",
        ["MMCDSP"] = "Multimedia changer / DSP",
        ["CCC"] = "Car communication computer",
        ["CIC"] = "Car information computer",
        ["KBM"] = "Body basic module",
        ["ZKE"] = "Central body electronics",
        ["EWS"] = "Electronic immobiliser",
        ["IKE"] = "Instrument cluster electronics",
        ["LCM"] = "Light check module",
        ["GM"] = "General module",
        ["SRS"] = "Airbag",
        ["ABS"] = "Anti-lock brakes",
        ["ASC"] = "Automatic stability control",
        ["EML"] = "Electronic throttle",
        ["SM"] = "Seat memory",
        ["IHKR"] = "Climate control",
        ["DDSHD"] = "Roof / sunroof",
        ["RCC"] = "Roll-over / convertible control",
        ["SVT"] = "Servotronic",
        ["ARS"] = "Active roll stabilisation (Dynamic Drive)",
        ["SBSL"] = "Satellite B-pillar left",
        ["SBSR"] = "Satellite B-pillar right",
        ["SASL"] = "Satellite A-pillar left",
        ["SASR"] = "Satellite A-pillar right",
        ["SSFA"] = "Satellite seat driver",
        ["SSBF"] = "Satellite seat passenger",
        ["STVL"] = "Satellite door front left",
        ["STVR"] = "Satellite door front right",
        ["SZM"] = "Centre console switch centre",
        ["CON"] = "Controller",
        ["NIVI"] = "Night vision",
        ["SHZH"] = "Seat heating rear",
        ["CA"] = "Comfort access",
        ["TCU"] = "Telematics control unit",
        ["ULF"] = "Universal hands-free",
        ["CVM"] = "Convertible top module",
        ["SMFA"] = "Seat module driver",
        ["SMBF"] = "Seat module passenger",
        ["EMA"] = "Electric belt tensioner",
        ["SWW"] = "Lane change warning",
        ["KAFAS"] = "Camera-based driver assistance",
        ["SVS"] = "Speech processing",
    };

    public static string Describe(string abbreviation)
    {
        if (s_names.TryGetValue(abbreviation, out var name))
        {
            return name;
        }

        // "FAH/BFH" style combinations: describe the first known part.
        foreach (var part in abbreviation.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (s_names.TryGetValue(part, out name))
            {
                return name;
            }
        }

        return abbreviation;
    }
}

/// <summary>Friendly names for E-series chassis codes.</summary>
public static class SeriesNames
{
    private static readonly Dictionary<string, string> s_names = new(StringComparer.OrdinalIgnoreCase)
    {
        ["E36"] = "3 Series (E36)",
        ["E38"] = "7 Series (E38)",
        ["E39"] = "5 Series (E39)",
        ["E46"] = "3 Series (E46)",
        ["E52"] = "Z8 (E52)",
        ["E53"] = "X5 (E53)",
        ["E60"] = "5 Series saloon (E60)",
        ["E61"] = "5 Series Touring (E61)",
        ["E63"] = "6 Series coupé (E63)",
        ["E64"] = "6 Series convertible (E64)",
        ["E65"] = "7 Series (E65)",
        ["E66"] = "7 Series long (E66)",
        ["E67"] = "7 Series armoured (E67)",
        ["E68"] = "7 Series hydrogen (E68)",
        ["E70"] = "X5 (E70)",
        ["E71"] = "X6 (E71)",
        ["E72"] = "X6 ActiveHybrid (E72)",
        ["E81"] = "1 Series 3-door (E81)",
        ["E82"] = "1 Series coupé (E82)",
        ["E83"] = "X3 (E83)",
        ["E84"] = "X1 (E84)",
        ["E85"] = "Z4 roadster (E85)",
        ["E86"] = "Z4 coupé (E86)",
        ["E87"] = "1 Series 5-door (E87)",
        ["E88"] = "1 Series convertible (E88)",
        ["E89"] = "Z4 roadster (E89)",
        ["E90"] = "3 Series saloon (E90)",
        ["E91"] = "3 Series Touring (E91)",
        ["E92"] = "3 Series coupé (E92)",
        ["E93"] = "3 Series convertible (E93)",
    };

    public static string Describe(string series) =>
        s_names.TryGetValue(series, out var name) ? name : series;
}

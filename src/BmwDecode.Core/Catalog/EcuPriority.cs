namespace BmwDecode.Core.Catalog;

/// <summary>
/// Importance ranking of control units: powertrain and safety first, comfort last.
/// Used to order the module list and the quick test.
/// </summary>
public static class EcuPriority
{
    // Order matters: the first matching group wins. Lower rank = more important.
    private static readonly (int Rank, string Label, string[] Names)[] s_ranks =
    [
        (1, "Engine", ["DME/DDE", "DME", "DDE", "DME2", "MOTOR", "DDE2"]),
        (2, "Transmission", ["EGS", "AGS", "GS", "SMG", "SSG", "GWS"]),
        (3, "Brakes / stability", ["DSC", "ABS", "ASC", "DXC", "EMF"]),
        (4, "Airbag", ["MRS", "ACSM", "SRS", "SIM", "SBSL", "SBSR", "SASL", "SASR"]),
        (5, "Car access / immobiliser", ["CAS", "EWS", "PGS", "CA"]),
        (6, "Instrument cluster", ["KOMBI", "IKE", "KOMB"]),
        (7, "Gateway / body", ["JBBF", "ZGM", "GM", "ZKE", "KBM", "SGM"]),
        (8, "Steering", ["EPS", "AFS", "SVT", "SZL", "ARS"]),
        (9, "Transfer case / chassis", ["VGSG", "LDM", "EDC", "EHC", "ACC", "RDC"]),
        (10, "Fuel / energy", ["EKP", "SME", "IBS"]),
        (11, "Lights / footwell", ["FRM", "LM", "LCM", "LSZ", "FLA"]),
        (12, "Climate", ["KLIMA", "IHKA", "IHKR", "IHR", "ZUH"]),
        (13, "Comfort / doors / seats", ["FZD", "RLS", "SHD", "SHD/MDS", "FAS", "BFS", "FAH", "BFH", "HKL", "CVM/CTM", "DWA", "SINE", "AHM", "PDC"]),
        (14, "Infotainment", ["CCC", "CIC", "MASK/CCC", "RADIO", "NAV", "CID", "ZBE", "TEL", "TEL/MULF", "AMP", "ASK", "DAB", "MOSTGW", "HUD", "CDC", "VIDEO"]),
    ];

    public static int Rank(string? abbreviation, int address = -1)
    {
        if (!string.IsNullOrWhiteSpace(abbreviation))
        {
            var name = abbreviation.Trim().ToUpperInvariant();
            foreach (var (rank, _, names) in s_ranks)
            {
                if (names.Contains(name) || names.Any(n => n.Length >= 3 && name.StartsWith(n, StringComparison.Ordinal)))
                {
                    return rank;
                }
            }
        }

        // Fall back to well-known diagnostic addresses (BN2000 and DS2).
        return address switch
        {
            0x12 or 0x13 or 0x14 => 1,
            0x18 or 0x32 => 2,
            0x29 or 0x56 => 3,
            0x01 or 0xA4 => 4,
            0x40 or 0x44 => 5,
            0x60 or 0x80 => 6,
            0x00 => 7,
            _ => 50,
        };
    }

    public static string Category(string? abbreviation, int address = -1)
    {
        var rank = Rank(abbreviation, address);
        return s_ranks.FirstOrDefault(r => r.Rank == rank).Label ?? "Other";
    }
}

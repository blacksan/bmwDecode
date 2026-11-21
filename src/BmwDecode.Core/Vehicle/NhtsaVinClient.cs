using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BmwDecode.Core.Vehicle;

/// <summary>Result of the US NHTSA vPIC VIN decoder (public, free, no key). Most complete for US-market cars.</summary>
public sealed record OnlineVinInfo(IReadOnlyList<KeyValuePair<string, string>> Fields, string? Message)
{
    public string? this[string key] => Fields.FirstOrDefault(f => f.Key == key).Value;
}

/// <summary>
/// Optional online decode via https://vpic.nhtsa.dot.gov (US government, public domain data).
/// Only called when the user asks for it.
/// </summary>
public sealed class NhtsaVinClient
{
    private static readonly (string Api, string Label)[] s_fields =
    [
        ("Make", "Make"), ("Model", "Model"), ("Series", "Series"), ("Trim", "Trim"), ("ModelYear", "Model year"),
        ("BodyClass", "Body"), ("Doors", "Doors"), ("DriveType", "Drive"), ("TransmissionStyle", "Transmission"),
        ("EngineModel", "Engine"), ("EngineConfiguration", "Engine layout"), ("EngineCylinders", "Cylinders"),
        ("DisplacementL", "Displacement (L)"), ("EngineKW", "Power (kW)"), ("FuelTypePrimary", "Fuel"),
        ("PlantCity", "Plant city"), ("PlantCountry", "Plant country"), ("Manufacturer", "Manufacturer"),
        ("VehicleType", "Vehicle type"),
    ];

    private readonly HttpClient _http;

    public NhtsaVinClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<OnlineVinInfo> DecodeAsync(string vin, CancellationToken cancellationToken = default)
    {
        var url = $"https://vpic.nhtsa.dot.gov/api/vehicles/DecodeVinValues/{Uri.EscapeDataString(vin)}?format=json";
        var response = await _http.GetFromJsonAsync<Response>(url, cancellationToken).ConfigureAwait(false);
        return Parse(response);
    }

    internal static OnlineVinInfo Parse(string json) => Parse(JsonSerializer.Deserialize<Response>(json));

    private static OnlineVinInfo Parse(Response? response)
    {
        var row = response?.Results?.FirstOrDefault();
        if (row is null)
        {
            return new OnlineVinInfo([], "No answer from NHTSA.");
        }

        var fields = new List<KeyValuePair<string, string>>();
        foreach (var (api, label) in s_fields)
        {
            if (row.TryGetValue(api, out var el) && el.ValueKind == JsonValueKind.String &&
                el.GetString() is { Length: > 0 } value && value != "Not Applicable")
            {
                fields.Add(new(label, value));
            }
        }

        var error = row.TryGetValue("ErrorText", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
        var code = row.TryGetValue("ErrorCode", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var message = code is null or "0" ? null : error;
        return new OnlineVinInfo(fields, message);
    }

    private sealed class Response
    {
        [JsonPropertyName("Results")]
        public List<Dictionary<string, JsonElement>>? Results { get; set; }
    }
}

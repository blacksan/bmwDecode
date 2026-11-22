using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Windows;
using BmwDecode.App.Services;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Services;
using BmwDecode.Core.Vehicle;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BmwDecode.App.ViewModels;

/// <summary>RealOEM-style VIN identification: offline structure decode, type key lookup, car data and online decode.</summary>
public sealed partial class VehicleViewModel : PageViewModel
{
    private readonly SessionService _session;
    private readonly NhtsaVinClient _nhtsa = new();
    private string? _lastAutoVin;

    public VehicleViewModel(SessionService session)
        : base("Vehicle / VIN", "")
    {
        _session = session;
    }

    public ObservableCollection<KeyValuePair<string, string>> VinRows { get; } = [];
    public ObservableCollection<KeyValuePair<string, string>> ModelRows { get; } = [];
    public ObservableCollection<KeyValuePair<string, string>> OnlineRows { get; } = [];
    public ObservableCollection<string> Options { get; } = [];
    public ObservableCollection<string> Sources { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DecodeCommand), nameof(OpenRealOemCommand), nameof(OnlineCommand))]
    private string _vinInput = string.Empty;

    [ObservableProperty]
    private string? _headline;

    [ObservableProperty]
    private string? _problems;

    [ObservableProperty]
    private string? _onlineMessage;

    [ObservableProperty]
    private string? _typeKeyInfo;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(UseSeriesCommand))]
    private SeriesDefinition? _decodedSeries;

    [ObservableProperty]
    private string? _shortVin;

    public string TypeKeyCsvPath => SessionService.TypeKeyCsvPath;

    public override void OnNavigatedTo()
    {
        // Pre-fill with the connected car's VIN unless the user typed something else.
        var carVin = _session.Vehicle.Vin;
        if (carVin is not null && (string.IsNullOrWhiteSpace(VinInput) || VinInput == _lastAutoVin))
        {
            VinInput = carVin;
            _lastAutoVin = carVin;
        }

        if (!string.IsNullOrWhiteSpace(VinInput))
        {
            _ = DecodeAsync();
        }
    }

    private bool HasInput() => !string.IsNullOrWhiteSpace(VinInput);

    [RelayCommand(CanExecute = nameof(HasInput))]
    private async Task DecodeAsync()
    {
        try
        {
            await DecodeCoreAsync();
        }
        catch (Exception ex)
        {
            Problems = ex.Message;
        }
    }

    private async Task DecodeCoreAsync()
    {
        var typeKeys = await _session.GetTypeKeysAsync();
        var car = _session.Vehicle;
        var d = VinDecoder.Decode(VinInput, car.Details.ProductionDate?.Year);
        var isConnectedCar = car.Vin is not null &&
            (car.Vin.Equals(d.Vin, StringComparison.OrdinalIgnoreCase) || car.ShortVin?.Equals(d.ShortVin, StringComparison.OrdinalIgnoreCase) == true);

        ShortVin = d.ShortVin;
        Problems = d.IsValid ? null : string.Join(" ", d.Problems);
        OnlineRows.Clear();
        OnlineMessage = null;

        VinRows.Clear();
        void Vin(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                VinRows.Add(new(label, value));
            }
        }

        Vin("VIN", d.Vin);
        Vin("Short VIN (last 7)", d.ShortVin);
        Vin("Manufacturer (1–3)", d.Wmi is null ? null : $"{d.Wmi} – {d.Manufacturer ?? "unknown"}{(d.Country is null ? string.Empty : $", {d.Country}")}");
        Vin("Type key (4–7)", d.TypeKey);
        Vin("Check digit (9)", d.CheckDigit is null ? null : d.IsNorthAmericanFormat ? $"{d.CheckDigit} – valid" : $"{d.CheckDigit} – not used on European VINs");
        Vin("Model year (10)", d.ModelYear?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? d.ModelYearNote);
        Vin("Plant (11)", d.PlantCode is null ? null : $"{d.PlantCode} – {d.Plant ?? "unknown plant code"}");
        Vin("Serial (12–17)", d.Serial);

        // Model: car data (when this is the connected car) + type key database.
        var details = isConnectedCar ? car.Details : VehicleDetails.Empty;
        // Factory VIN range lookups read a band of the data pack: keep them off the UI thread.
        var vinToFind = d.Vin ?? d.ShortVin;
        var seen = await Task.Run(() => typeKeys.FindVin(vinToFind));
        if (seen is not null)
        {
            details = details with
            {
                TypeKey = details.TypeKey ?? seen.TypeKey,
                Model = details.Model ?? seen.Model,
                Engine = details.Engine ?? seen.EngineCode ?? seen.Engine,
                Gearbox = details.Gearbox ?? seen.Gearbox,
                Drive = details.Drive ?? seen.Drive,
                Displacement = details.Displacement ?? seen.Displacement,
                Body = details.Body ?? seen.Body,
                Market = details.Market ?? seen.Market,
                Steering = details.Steering ?? seen.Steering,
                ProductionDate = details.ProductionDate ?? seen.Production,
            };
        }

        var typeKey = details.TypeKey ?? d.TypeKey;
        var tk = typeKeys.Find(typeKey);
        if (tk is not null)
        {
            details = VehicleIdentifier.Merge(details with { TypeKey = typeKey }, tk);
        }
        else if (typeKey is not null && details.TypeKey is null)
        {
            details = details with { TypeKey = typeKey };
        }

        ModelRows.Clear();
        var seriesText = seen?.Series ?? (isConnectedCar ? car.Series : null) ?? tk?.Series;
        if (seriesText is not null)
        {
            ModelRows.Add(new("Series", SeriesNames.Describe(seriesText)));
        }

        foreach (var row in details.Rows())
        {
            ModelRows.Add(row);
        }

        if (tk?.Production is { } prod && details.ProductionDate is null)
        {
            ModelRows.Add(new("Built", prod));
        }

        Options.Clear();
        foreach (var o in details.Options)
        {
            Options.Add(o);
        }

        Sources.Clear();
        foreach (var s in isConnectedCar ? car.Details.Sources : [])
        {
            Sources.Add(s);
        }

        if (seen is not null)
        {
            Sources.Insert(0, seen.IsFactoryRange
                ? $"VIN found in BMW's factory VIN ranges (built-in data){(seen.Production is { } built ? $", built {built:MM/yyyy}" : string.Empty)}"
                : $"VIN identified by ISTA on this PC{(seen.SeenAt is { } at ? $" ({at:yyyy-MM-dd})" : string.Empty)} – factory specification");
        }

        if (tk is not null && !Sources.Any(s => s.Contains(tk.Source, StringComparison.OrdinalIgnoreCase)))
        {
            Sources.Add($"Type key {tk.TypeKey} from {tk.Source}");
        }

        DecodedSeries = _session.Catalog.Find(seriesText);
        Headline = (seen?.Model ?? tk?.Model) is { } model
            ? $"{SeriesNames.Describe(seriesText ?? "?")} · {model}"
            : seriesText is not null ? SeriesNames.Describe(seriesText) : d.IsValid ? "BMW – model not in the local type key list" : null;

        TypeKeyInfo = typeKeys.Count == 0
            ? "No type key data loaded. Set the EDIABAS folder (FZGIDENT.PRG) or add typekeys.csv."
            : $"Type key data: {string.Join(", ", typeKeys.Sources)}." +
              (tk is null && typeKey is not null ? $" {typeKey} is not listed – add it to typekeys.csv, or use RealOEM / the online decode." : string.Empty);
    }

    [RelayCommand(CanExecute = nameof(HasInput), IncludeCancelCommand = true)]
    private async Task OnlineAsync(CancellationToken cancellationToken)
    {
        var d = VinDecoder.Decode(VinInput);
        if (d.Vin is null)
        {
            OnlineMessage = "The online decode needs the full 17-character VIN.";
            return;
        }

        OnlineMessage = "Asking NHTSA vPIC…";
        OnlineRows.Clear();
        try
        {
            var info = await _nhtsa.DecodeAsync(d.Vin, cancellationToken);
            foreach (var f in info.Fields)
            {
                OnlineRows.Add(f);
            }

            OnlineMessage = info.Fields.Count == 0
                ? info.Message ?? "No data returned."
                : (info.Message is null ? "Decoded by NHTSA vPIC (most complete for US-market cars)." : $"NHTSA: {info.Message}");
        }
        catch (OperationCanceledException)
        {
            OnlineMessage = "Cancelled.";
        }
        catch (Exception ex) when (ex is HttpRequestException or System.Text.Json.JsonException or TaskCanceledException)
        {
            OnlineMessage = $"Online decode failed: {ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(HasInput))]
    private void OpenRealOem()
    {
        var shortVin = VinDecoder.Decode(VinInput).ShortVin;
        Open($"https://www.realoem.com/bmw/enUS/select?vin={Uri.EscapeDataString(shortVin)}");
    }

    [RelayCommand]
    private void CopyShortVin()
    {
        if (ShortVin is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(ShortVin);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Problems = "Clipboard is busy – try again.";
        }
    }

    private bool CanUseSeries() => DecodedSeries is not null && !ReferenceEquals(DecodedSeries, _session.Series);

    [RelayCommand(CanExecute = nameof(CanUseSeries))]
    private void UseSeries()
    {
        _session.Series = DecodedSeries;
        UseSeriesCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private void OpenTypeKeyFile()
    {
        try
        {
            Directory.CreateDirectory(SettingsStore.Folder);
            if (!File.Exists(TypeKeyCsvPath))
            {
                File.WriteAllText(TypeKeyCsvPath,
                    "# bmwDecode type keys: one line per BMW type key (VIN positions 4-7). Lines starting with # are ignored.\r\n" +
                    "# Entries here override the FZGIDENT.PRG table. Save the file, then press Decode again.\r\n" +
                    TypeKeyDatabase.CsvHeader + "\r\n");
            }

            Open(TypeKeyCsvPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Problems = ex.Message;
        }
    }

    private void Open(string target)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(target) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Problems = ex.Message;
        }
    }
}

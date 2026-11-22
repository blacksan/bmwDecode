using System.Collections.ObjectModel;
using BmwDecode.App.Services;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BmwDecode.App.ViewModels;

public sealed record ModuleListItem(EcuDefinition Ecu, string? Variant, EcuScanState State, int FaultCount)
{
    public string Caption => $"{Ecu.Abbreviation}";
    public string Category => $"{Ecu.Priority:00} · {EcuPriority.Category(Ecu.Abbreviation, Ecu.Address)}";
    public string Detail => Variant is null ? Ecu.Description : $"{Ecu.Description} · {Variant}";
    public string Badge => State switch
    {
        EcuScanState.Faults => FaultCount.ToString(System.Globalization.CultureInfo.CurrentCulture),
        _ => string.Empty,
    };
}

/// <summary>Master/detail list of modules. Uses the last quick test when available, else the whole chassis.</summary>
public sealed partial class ModulesViewModel : PageViewModel
{
    private readonly SessionService _session;
    private readonly SettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly Dictionary<string, ModuleViewModel> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModulesViewModel(SessionService session, SettingsStore settings, IDialogService dialogs)
        : base("Modules", "")
    {
        _session = session;
        _settings = settings;
        _dialogs = dialogs;
        _session.ConnectionChanged += (_, _) =>
        {
            foreach (var vm in _cache.Values)
            {
                vm.StopLive();
            }

            _cache.Clear();
            Module = null;
        };
    }

    public ObservableCollection<ModuleListItem> Items { get; } = [];

    [ObservableProperty]
    private ModuleListItem? _selectedItem;

    [ObservableProperty]
    private ModuleViewModel? _module;

    [ObservableProperty]
    private string? _hint;

    public override void OnNavigatedTo() => Rebuild();

    /// <summary>Open a module directly (from the quick test).</summary>
    public void Open(EcuDefinition ecu, string? variant, IReadOnlyList<FaultEntry>? faults)
    {
        Rebuild();
        var item = Items.FirstOrDefault(i => i.Ecu.Key == ecu.Key);
        if (item is null)
        {
            item = new ModuleListItem(ecu, variant, faults is { Count: > 0 } ? EcuScanState.Faults : EcuScanState.NoFaults, faults?.Count ?? 0);
            Items.Insert(0, item);
        }

        if (_cache.Remove(ecu.Key, out var stale))
        {
            stale.StopLive();
        }

        // Reset first: records compare by value, so re-selecting the same module would otherwise be a no-op.
        SelectedItem = null;
        SelectedItem = item;
    }

    partial void OnSelectedItemChanged(ModuleListItem? value)
    {
        if (value is null)
        {
            Module = null;
            return;
        }

        if (!_cache.TryGetValue(value.Ecu.Key, out var vm))
        {
            var faults = _session.LastReport?.Results.FirstOrDefault(r => r.Ecu.Key == value.Ecu.Key)?.Faults;
            vm = new ModuleViewModel(value.Ecu, value.Variant, faults, _session, _settings, _dialogs);
            _cache[value.Ecu.Key] = vm;
        }

        Module = vm;
        _ = InitialiseSafeAsync(vm);
    }

    private async Task InitialiseSafeAsync(ModuleViewModel vm)
    {
        try
        {
            await vm.InitialiseAsync();
        }
        catch (Exception ex)
        {
            vm.StatusText = ex.Message;
            _session.Error($"{vm.Ecu.Abbreviation}: {ex.Message}");
        }
    }

    private void Rebuild()
    {
        var selectedKey = SelectedItem?.Ecu.Key;
        Items.Clear();

        if (_session.LastReport is { } report && report.Series == _session.Series?.Series)
        {
            foreach (var r in report.Results.Where(r => r.IsFitted).OrderBy(r => r.Ecu.Priority).ThenBy(r => r.Ecu.Address))
            {
                Items.Add(new ModuleListItem(r.Ecu, r.Variant, r.State, r.FaultList.Count));
            }

            Hint = $"{Items.Count} modules – {report.DiscoveryNote ?? "from the last quick test"}. Ranked by importance.";
        }
        else if (_session.Series is { } series)
        {
            foreach (var e in series.Ecus.GroupBy(e => e.PrimaryGroup).Select(g => g.First()).OrderBy(e => e.Priority).ThenBy(e => e.Address))
            {
                Items.Add(new ModuleListItem(e, null, EcuScanState.Pending, 0));
            }

            Hint = $"Not scanned yet – showing all {Items.Count} possible modules of the {series.Series}. Connect and the list fills with what the car reports.";
        }

        SelectedItem = Items.FirstOrDefault(i => i.Ecu.Key == selectedKey);
    }
}

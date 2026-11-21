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
    private readonly DialogService _dialogs;
    private readonly Dictionary<string, ModuleViewModel> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ModulesViewModel(SessionService session, SettingsStore settings, DialogService dialogs)
        : base("Modules", "")
    {
        _session = session;
        _settings = settings;
        _dialogs = dialogs;
        _session.ConnectionChanged += (_, _) =>
        {
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

        _cache.Remove(ecu.Key);
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
        _ = vm.InitialiseAsync();
    }

    private void Rebuild()
    {
        var selectedKey = SelectedItem?.Ecu.Key;
        Items.Clear();

        if (_session.LastReport is { } report && report.Series == _session.Series?.Series)
        {
            foreach (var r in report.Results.Where(r => r.IsFitted).OrderBy(r => r.Ecu.Address))
            {
                Items.Add(new ModuleListItem(r.Ecu, r.Variant, r.State, r.FaultList.Count));
            }

            Hint = $"{Items.Count} modules found by the last quick test.";
        }
        else if (_session.Series is { } series)
        {
            foreach (var e in series.Ecus.GroupBy(e => e.PrimaryGroup).Select(g => g.First()))
            {
                Items.Add(new ModuleListItem(e, null, EcuScanState.Pending, 0));
            }

            Hint = $"All {Items.Count} module slots of the {series.Series}. Run a quick test to list only the fitted ones.";
        }

        SelectedItem = Items.FirstOrDefault(i => i.Ecu.Key == selectedKey);
    }
}

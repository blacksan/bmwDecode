using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Data;
using BmwDecode.App.Services;
using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BmwDecode.App.ViewModels;

public sealed partial class LiveJobOption : ObservableObject
{
    public LiveJobOption(string name, string comment, bool selected)
    {
        Name = name;
        Comment = comment;
        _isSelected = selected;
    }

    public string Name { get; }
    public string Comment { get; }

    [ObservableProperty]
    private bool _isSelected;
}

public sealed partial class LiveRow : ObservableObject
{
    public LiveRow(LiveValue v)
    {
        Key = v.Key;
        Job = v.Job;
        Label = v.Label;
        Update(v);
    }

    public string Key { get; }
    public string Job { get; }
    public string Label { get; }

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private string? _unit;

    [ObservableProperty]
    private string? _min;

    [ObservableProperty]
    private string? _max;

    private double? _minValue;
    private double? _maxValue;

    public void Update(LiveValue v)
    {
        Value = v.Value;
        Unit = v.Unit;
        if (v.Numeric is { } n)
        {
            _minValue = _minValue is null ? n : Math.Min(_minValue.Value, n);
            _maxValue = _maxValue is null ? n : Math.Max(_maxValue.Value, n);
            Min = _minValue.Value.ToString("0.##", CultureInfo.CurrentCulture);
            Max = _maxValue.Value.ToString("0.##", CultureInfo.CurrentCulture);
        }
    }
}

/// <summary>A job of this module, with its category and translated description.</summary>
public sealed record ModuleJobItem(string Category, int CategoryOrder, JobDescription Job, string Comment)
{
    public string Name => Job.Name;
    public bool IsWriting => Job.IsPotentiallyDestructive;
}

/// <summary>Everything about one control unit: identification, fault and info memory, live values.</summary>
public sealed partial class ModuleViewModel : ObservableObject
{
    private readonly SessionService _session;
    private readonly SettingsStore _settings;
    private readonly DialogService _dialogs;
    private readonly Dictionary<string, LiveRow> _liveByKey = new(StringComparer.OrdinalIgnoreCase);

    public ModuleViewModel(EcuDefinition ecu, string? variant, IReadOnlyList<FaultEntry>? knownFaults,
        SessionService session, SettingsStore settings, DialogService dialogs)
    {
        Ecu = ecu;
        _variant = variant;
        _session = session;
        _settings = settings;
        _dialogs = dialogs;
        foreach (var f in knownFaults ?? [])
        {
            Faults.Add(f);
        }

        ModuleJobsView = CollectionViewSource.GetDefaultView(ModuleJobs);
        ModuleJobsView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ModuleJobItem.Category)));
        ModuleJobsView.SortDescriptions.Add(new SortDescription(nameof(ModuleJobItem.CategoryOrder), ListSortDirection.Ascending));
        ModuleJobsView.SortDescriptions.Add(new SortDescription(nameof(ModuleJobItem.Name), ListSortDirection.Ascending));
        ModuleJobsView.Filter = o => o is ModuleJobItem j && (string.IsNullOrWhiteSpace(JobFilter) ||
            j.Name.Contains(JobFilter, StringComparison.OrdinalIgnoreCase) || j.Comment.Contains(JobFilter, StringComparison.OrdinalIgnoreCase));
    }

    // ---------------- Jobs & coding ----------------

    public ObservableCollection<ModuleJobItem> ModuleJobs { get; } = [];
    public ICollectionView ModuleJobsView { get; }
    public ObservableCollection<ResultRow> ModuleJobResults { get; } = [];
    public ObservableCollection<ParameterDescription> ModuleJobArguments { get; } = [];

    [ObservableProperty]
    private string _jobFilter = string.Empty;

    [ObservableProperty]
    private ModuleJobItem? _selectedModuleJob;

    [ObservableProperty]
    private string? _moduleJobComment;

    [ObservableProperty]
    private string _moduleJobArgs = string.Empty;

    [ObservableProperty]
    private string? _moduleJobStatus;

    partial void OnJobFilterChanged(string value) => ModuleJobsView.Refresh();

    [RelayCommand]
    private Task LoadJobsAsync() => Busy("Loading the module's jobs…", async ct =>
    {
        await ResolveAsync(ct);
        var inspector = _session.InspectorFor(_session.Engine);
        var description = await inspector.DescribeAsync(Variant!, ct);
        var tr = Core.Translation.Translator.Current;
        ModuleJobs.Clear();
        foreach (var job in description.Jobs)
        {
            var category = JobCategories.Of(job.Name);
            ModuleJobs.Add(new ModuleJobItem(category, Array.IndexOf(JobCategories.Order, category), job, tr.Translate(job.Comment)));
        }

        var coding = ModuleJobs.Count(j => j.Category == JobCategories.Coding);
        ModuleJobStatus = $"{ModuleJobs.Count} jobs in {description.Name}; {coding} coding job(s). Writing jobs ask for confirmation.";
    });

    partial void OnSelectedModuleJobChanged(ModuleJobItem? value)
    {
        ModuleJobArguments.Clear();
        ModuleJobComment = value?.Comment;
        if (value is null || Variant is null)
        {
            return;
        }

        _ = LoadJobDetailAsync(value);
    }

    private async Task LoadJobDetailAsync(ModuleJobItem item)
    {
        try
        {
            var details = await _session.InspectorFor(_session.Engine).DescribeJobAsync(Variant!, item.Name);
            if (!ReferenceEquals(SelectedModuleJob, item))
            {
                return;
            }

            var tr = Core.Translation.Translator.Current;
            ModuleJobComment = tr.Translate(details.Comment);
            foreach (var a in details.Arguments)
            {
                ModuleJobArguments.Add(a with { Comment = tr.Translate(a.Comment) });
            }
        }
        catch (Exception ex)
        {
            ModuleJobStatus = ex.Message;
        }
    }

    [RelayCommand]
    private Task RunModuleJobAsync()
    {
        if (SelectedModuleJob is not { } item)
        {
            return Task.CompletedTask;
        }

        if (item.IsWriting && !_dialogs.Confirm($"Run {item.Name} on {Ecu.Abbreviation}?",
                $"{item.Name} ({item.Category}) can change this module: coding, adaptations, actuators, resets or flashing.\n\n" +
                $"{item.Comment}\n\nBack up the coding first (\"Back up coding\"), keep a battery charger connected and only continue if you know what this job does."))
        {
            return Task.CompletedTask;
        }

        return Busy($"Running {item.Name}…", async ct =>
        {
            ModuleJobResults.Clear();
            var r = await RunAsync(item.Name, ModuleJobArgs.Trim(), ct);
            var tr = Core.Translation.Translator.Current;
            for (var i = 0; i < r.Sets.Count; i++)
            {
                foreach (var v in r.Sets[i].Ordered)
                {
                    var display = v.Kind == ResultKind.Text ? tr.Translate(v.Display) : v.Display;
                    ModuleJobResults.Add(new ResultRow(i + 1, v.Name, display, v.KindLabel));
                }
            }

            ModuleJobStatus = $"{r.Variant}.{item.Name}: {r.JobStatus ?? "no JOB_STATUS"} · {r.Sets.Count} set(s) · {r.Duration.TotalMilliseconds:0} ms";
            _session.Info($"{Ecu.Abbreviation}: {ModuleJobStatus}");
        });
    }

    /// <summary>Runs every argument-free coding read job (C_*_LESEN) and saves the raw results, as a backup before coding.</summary>
    [RelayCommand]
    private Task BackupCodingAsync() => Busy("Reading coding data…", async ct =>
    {
        if (ModuleJobs.Count == 0)
        {
            await LoadJobsCore(ct);
        }

        var inspector = _session.InspectorFor(_session.Engine);
        var sb = new StringBuilder();
        sb.AppendLine($"# bmwDecode coding backup – {Ecu.Abbreviation} ({Variant}) – {DateTime.Now:yyyy-MM-dd HH:mm}");
        sb.AppendLine($"# VIN {_session.Vehicle.Vin ?? "unknown"}");
        var read = 0;
        foreach (var job in ModuleJobs.Where(j => JobCategories.IsCodingRead(j.Name)))
        {
            var details = await inspector.DescribeJobAsync(Variant!, job.Name, ct);
            if (details.Arguments.Count > 0)
            {
                continue;
            }

            try
            {
                var r = await RunAsync(job.Name, "", ct);
                sb.AppendLine().AppendLine($"[{job.Name}] {r.JobStatus}");
                for (var i = 0; i < r.Sets.Count; i++)
                {
                    foreach (var v in r.Sets[i].Ordered)
                    {
                        sb.AppendLine($"{i + 1}\t{v.Name}\t{v.Display}");
                    }
                }

                read++;
            }
            catch (DiagnosticException ex)
            {
                sb.AppendLine().AppendLine($"[{job.Name}] failed: {ex.Message}");
            }
        }

        if (read == 0)
        {
            ModuleJobStatus = "This module has no argument-free coding read jobs.";
            return;
        }

        var path = _dialogs.SaveFile("Save coding backup", $"coding_{Ecu.Abbreviation.Replace('/', '-')}_{Variant}_{DateTime.Now:yyyyMMdd_HHmm}.txt", "Text file|*.txt");
        if (path is not null)
        {
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
            ModuleJobStatus = $"Coding backup ({read} jobs) saved to {path}";
        }
    });

    private async Task LoadJobsCore(CancellationToken ct)
    {
        await ResolveAsync(ct);
        var description = await _session.InspectorFor(_session.Engine).DescribeAsync(Variant!, ct);
        var tr = Core.Translation.Translator.Current;
        foreach (var job in description.Jobs)
        {
            var category = JobCategories.Of(job.Name);
            ModuleJobs.Add(new ModuleJobItem(category, Array.IndexOf(JobCategories.Order, category), job, tr.Translate(job.Comment)));
        }
    }

    public EcuDefinition Ecu { get; }
    public string Title => $"{Ecu.Abbreviation} · {Ecu.Description}";

    public ObservableCollection<IdentItem> Ident { get; } = [];
    public ObservableCollection<FaultEntry> Faults { get; } = [];
    public ObservableCollection<FaultEntry> InfoEntries { get; } = [];
    public ObservableCollection<EnvironmentCondition> DetailConditions { get; } = [];
    public ObservableCollection<KeyValuePair<string, string>> DetailRaw { get; } = [];
    public ObservableCollection<LiveJobOption> LiveJobs { get; } = [];
    public ObservableCollection<LiveRow> LiveRows { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Subtitle))]
    private string? _variant;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private FaultEntry? _selectedFault;

    [ObservableProperty]
    private string? _detailSummary;

    [ObservableProperty]
    private bool _isLive;

    [ObservableProperty]
    private string? _liveStatus;

    public string Subtitle => $"Address {Ecu.AddressHex} · group {string.Join(" / ", Ecu.Groups)}" + (Variant is null ? string.Empty : $" · SGBD {Variant}");

    /// <summary>Loads identification and fault memory the first time the module is shown.</summary>
    public async Task InitialiseAsync()
    {
        if (Ident.Count == 0 && _session.IsConnected)
        {
            await RefreshAllAsync();
        }
    }

    [RelayCommand]
    private Task RefreshAllAsync() => Busy("Reading module…", async ct =>
    {
        await ResolveAsync(ct);
        await ReadIdentCoreAsync(ct);
        await ReadFaultsCoreAsync(ct);
        await ReadInfoCoreAsync(ct);
    });

    [RelayCommand]
    private Task ReadFaultsAsync() => Busy("Reading fault memory…", ReadFaultsCoreAsync);

    [RelayCommand]
    private Task ReadInfoAsync() => Busy("Reading info memory…", ReadInfoCoreAsync);

    [RelayCommand]
    private Task ClearFaultsAsync()
    {
        if (!_dialogs.Confirm($"Clear {Ecu.Abbreviation} fault memory",
                $"Clear all {Faults.Count} fault(s) stored in {Ecu.Abbreviation}?\nFaults that are still present will be stored again."))
        {
            return Task.CompletedTask;
        }

        return Busy("Clearing fault memory…", async ct =>
        {
            var r = await RunAsync("FS_LOESCHEN", "", ct);
            StatusText = r.IsOkay ? "Fault memory cleared." : $"FS_LOESCHEN: {r.JobStatus}";
            _session.Info($"{Ecu.Abbreviation}: {StatusText}");
            await ReadFaultsCoreAsync(ct);
        });
    }

    [RelayCommand]
    private Task ClearInfoAsync()
    {
        if (!_dialogs.Confirm($"Clear {Ecu.Abbreviation} info memory", $"Clear the info (shadow) memory of {Ecu.Abbreviation}?"))
        {
            return Task.CompletedTask;
        }

        return Busy("Clearing info memory…", async ct =>
        {
            var r = await RunAsync("IS_LOESCHEN", "", ct);
            StatusText = r.IsOkay ? "Info memory cleared." : $"IS_LOESCHEN: {r.JobStatus}";
            await ReadInfoCoreAsync(ct);
        });
    }

    partial void OnSelectedFaultChanged(FaultEntry? value)
    {
        DetailConditions.Clear();
        DetailRaw.Clear();
        DetailSummary = null;
        if (value is null)
        {
            return;
        }

        foreach (var kv in value.Raw)
        {
            DetailRaw.Add(kv);
        }

        if (_session.IsConnected && !IsBusy)
        {
            _ = Busy("Reading fault details…", ct => LoadDetailCoreAsync(value, ct));
        }
    }

    /// <summary>Stops a running live-data poll (called on disconnect).</summary>
    public void StopLive()
    {
        if (StartLiveCancelCommand.CanExecute(null))
        {
            StartLiveCancelCommand.Execute(null);
        }
    }

    private async Task LoadDetailCoreAsync(FaultEntry fault, CancellationToken ct)
    {
        // DS2 (K-line) SGBDs have no FS_LESEN_DETAIL: their environment conditions arrive with FS_LESEN itself.
        FaultDetail detail;
        JobResult? r = null;
        try
        {
            r = await RunAsync("FS_LESEN_DETAIL", fault.Code.ToString(CultureInfo.InvariantCulture), ct);
        }
        catch (DiagnosticException)
        {
            // fall through to the FS_LESEN data
        }

        if (!ReferenceEquals(SelectedFault, fault))
        {
            return; // selection moved on while the ECU answered
        }

        if (r is { IsOkay: true })
        {
            detail = FaultParser.ParseDetail(fault.Code, r);
        }
        else
        {
            detail = FaultParser.DetailFromEntry(fault);
            if (detail.Conditions.Count == 0)
            {
                DetailSummary = r is null ? "This module has no detailed fault data." : $"No details available ({r.JobStatus}).";
                return;
            }
        }

        DetailConditions.Clear();
        foreach (var c in detail.Conditions)
        {
            DetailConditions.Add(c);
        }

        DetailRaw.Clear();
        foreach (var kv in detail.Raw)
        {
            DetailRaw.Add(kv);
        }

        var parts = new List<string>();
        if (detail.Mileage is { } km)
        {
            parts.Add($"at {km:N0} km");
        }

        if (detail.Frequency is { } n)
        {
            parts.Add($"occurred {n}×");
        }

        if (detail.LogisticsCounter is { } lz)
        {
            parts.Add($"logistics counter {lz}");
        }

        DetailSummary = parts.Count == 0 ? null : "Stored " + string.Join(", ", parts);
    }

    // ---------------- Live data ----------------

    [RelayCommand]
    private Task LoadLiveJobsAsync() => Busy("Loading live-data jobs…", async ct =>
    {
        await ResolveAsync(ct);
        var inspector = _session.InspectorFor(_session.Engine);
        var description = await inspector.DescribeAsync(Variant!, ct);
        _settings.Current.LiveSelections.TryGetValue(Variant!, out var saved);

        LiveJobs.Clear();
        foreach (var job in description.Jobs.Where(j => j.Name.StartsWith("STATUS_", StringComparison.OrdinalIgnoreCase)))
        {
            var details = await inspector.DescribeJobAsync(Variant!, job.Name, ct);
            if (details.Arguments.Count > 0 || job.IsPotentiallyDestructive)
            {
                continue; // live view only polls argument-free read jobs
            }

            LiveJobs.Add(new LiveJobOption(job.Name, Core.Translation.Translator.Current.Translate(job.Comment),
                saved?.Contains(job.Name, StringComparer.OrdinalIgnoreCase) == true));
        }

        LiveStatus = LiveJobs.Count == 0 ? "This SGBD has no argument-free STATUS jobs." : $"{LiveJobs.Count} live-data jobs. Tick the ones to watch, then Start.";
    });

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task StartLiveAsync(CancellationToken cancellationToken)
    {
        var jobs = LiveJobs.Where(j => j.IsSelected).Select(j => j.Name).ToList();
        if (jobs.Count == 0 || Variant is null)
        {
            LiveStatus = "Tick at least one job first.";
            return;
        }

        _settings.Current.LiveSelections[Variant] = jobs;
        _settings.Save(); // best effort
        LiveRows.Clear();
        _liveByKey.Clear();
        IsLive = true;
        _session.IsBusy = true;
        var interval = TimeSpan.FromMilliseconds(Math.Clamp(_settings.Current.LiveIntervalMs, 100, 10_000));
        var rounds = 0;
        try
        {
            await foreach (var sample in new LiveDataPoller(_session.RequireEngine()).PollAsync(Variant, jobs, interval, cancellationToken))
            {
                foreach (var v in sample.Values)
                {
                    if (_liveByKey.TryGetValue(v.Job + "|" + v.Key, out var row))
                    {
                        row.Update(v);
                    }
                    else
                    {
                        row = new LiveRow(v);
                        _liveByKey[v.Job + "|" + v.Key] = row;
                        LiveRows.Add(row);
                    }
                }

                rounds++;
                LiveStatus = sample.Errors.Count == 0
                    ? $"Live · {sample.Time:HH:mm:ss} · round {rounds}"
                    : $"Live · {sample.Time:HH:mm:ss} · {string.Join("; ", sample.Errors.Select(e => $"{e.Key}: {e.Value}"))}";
            }
        }
        catch (DiagnosticException ex)
        {
            LiveStatus = ex.Message;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LiveStatus = ex.Message;
        }
        finally
        {
            IsLive = false;
            _session.IsBusy = false;
            if (cancellationToken.IsCancellationRequested)
            {
                LiveStatus = $"Stopped after {rounds} rounds.";
            }
        }
    }

    [RelayCommand]
    private void ClearLiveSelection()
    {
        foreach (var j in LiveJobs)
        {
            j.IsSelected = false;
        }
    }

    // ---------------- helpers ----------------

    private async Task ResolveAsync(CancellationToken ct)
    {
        if (Variant is null)
        {
            Variant = await _session.RequireEngine().ResolveVariantAsync(Ecu.PrimaryGroup, ct);
        }
    }

    private async Task ReadIdentCoreAsync(CancellationToken ct)
    {
        var r = await RunAsync("IDENT", "", ct);
        Ident.Clear();
        foreach (var item in FaultParser.ParseIdent(r))
        {
            Ident.Add(item);
        }
    }

    private async Task ReadFaultsCoreAsync(CancellationToken ct)
    {
        var r = await RunAsync("FS_LESEN", "", ct);
        Faults.Clear();
        foreach (var f in FaultParser.ParseFaults(r))
        {
            Faults.Add(f);
        }

        StatusText = Faults.Count == 0 ? "No faults stored." : $"{Faults.Count} fault(s) stored.";
    }

    private async Task ReadInfoCoreAsync(CancellationToken ct)
    {
        try
        {
            var r = await RunAsync("IS_LESEN", "", ct);
            InfoEntries.Clear();
            foreach (var f in FaultParser.ParseFaults(r))
            {
                InfoEntries.Add(f);
            }
        }
        catch (DiagnosticException)
        {
            // Many ECUs have no info memory: not an error worth surfacing.
            InfoEntries.Clear();
        }
    }

    private async Task<JobResult> RunAsync(string job, string args, CancellationToken ct)
    {
        await ResolveAsync(ct);
        return await _session.RequireEngine().ExecuteAsync(Variant!, job, args, ct);
    }

    private async Task Busy(string text, Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        IsBusy = true;
        StatusText = text;
        try
        {
            await action(CancellationToken.None);
            if (StatusText == text)
            {
                StatusText = null;
            }
        }
        catch (DiagnosticException ex)
        {
            StatusText = ex.IsNoResponse ? $"{Ecu.Abbreviation} does not answer ({ex.Message})." : ex.Message;
            _session.Warn($"{Ecu.Abbreviation}: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            StatusText = "Cancelled.";
        }
        catch (Exception ex)
        {
            // Last resort for fire-and-forget callers: never let an unexpected error escape unobserved.
            StatusText = ex.Message;
            _session.Error($"{Ecu.Abbreviation}: {ex}");
        }
        finally
        {
            IsBusy = false;
        }
    }
}

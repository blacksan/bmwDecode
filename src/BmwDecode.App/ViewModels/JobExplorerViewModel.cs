using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Data;
using BmwDecode.App.Services;
using BmwDecode.Core.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace BmwDecode.App.ViewModels;

public sealed record ResultRow(int Set, string Name, string Value, string Type);

/// <summary>Tool32-style explorer: browse any SGBD, read its job documentation and run jobs with arguments.</summary>
public sealed partial class JobExplorerViewModel : PageViewModel
{
    private readonly SessionService _session;
    private readonly DialogService _dialogs;
    private ISgbdInspector? _inspector;
    private CancellationTokenSource? _loadCts;

    public JobExplorerViewModel(SessionService session, DialogService dialogs)
        : base("Job explorer", "")
    {
        _session = session;
        _dialogs = dialogs;
        FilesView = CollectionViewSource.GetDefaultView(Files);
        FilesView.Filter = o => o is SgbdFile f && Matches(f.Name, FileFilter);
        JobsView = CollectionViewSource.GetDefaultView(Jobs);
        JobsView.Filter = o => o is JobDescription j && (Matches(j.Name, JobFilter) || Matches(j.Comment, JobFilter));
        _session.ConnectionChanged += (_, _) => RunCommand.NotifyCanExecuteChanged();
    }

    public ObservableCollection<SgbdFile> Files { get; } = [];
    public ICollectionView FilesView { get; }
    public ObservableCollection<JobDescription> Jobs { get; } = [];
    public ICollectionView JobsView { get; }
    public ObservableCollection<ResultRow> Results { get; } = [];

    [ObservableProperty]
    private string _fileFilter = string.Empty;

    [ObservableProperty]
    private string _jobFilter = string.Empty;

    [ObservableProperty]
    private SgbdFile? _selectedFile;

    [ObservableProperty]
    private string? _loadedName;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private JobDescription? _selectedJob;

    [ObservableProperty]
    private JobDescription? _jobDetails;

    [ObservableProperty]
    private string _arguments = string.Empty;

    [ObservableProperty]
    private string? _resultSummary;

    partial void OnFileFilterChanged(string value) => FilesView.Refresh();

    partial void OnJobFilterChanged(string value) => JobsView.Refresh();

    public override void OnNavigatedTo()
    {
        var inspector = _session.FileInspector;
        if (!ReferenceEquals(inspector, _inspector) || Files.Count == 0)
        {
            _inspector = inspector;
            Files.Clear();
            foreach (var f in inspector.ListFiles())
            {
                Files.Add(f);
            }

            StatusText = inspector is Ediabas.SgbdInspector
                ? $"{Files.Count:N0} SGBD files. Variant files (.prg) can be browsed offline; group files (.grp) are identified on the car."
                : "No EDIABAS folder set – showing the demo vehicle's SGBDs. Set the folder in Settings to browse your INPA files.";
        }
    }

    partial void OnSelectedFileChanged(SgbdFile? value)
    {
        _loadCts?.Cancel();
        if (value is not null)
        {
            _loadCts = new CancellationTokenSource();
            _ = LoadFileAsync(value, _loadCts.Token);
        }
    }

    private async Task LoadFileAsync(SgbdFile file, CancellationToken ct)
    {
        Jobs.Clear();
        JobDetails = null;
        LoadedName = null;
        IsWorking = true;
        try
        {
            var name = file.Name;
            if (file.IsGroup)
            {
                if (!_session.IsConnected)
                {
                    StatusText = $"{file.Name} is a group file: connect to the car so it can be identified, or pick the variant .prg directly.";
                    return;
                }

                name = await _session.RequireEngine().ResolveVariantAsync(file.Name, ct);
            }

            var description = await _inspector!.DescribeAsync(name, ct);
            if (ct.IsCancellationRequested)
            {
                return;
            }

            LoadedName = file.IsGroup ? $"{file.Name} → {description.Name}" : description.Name;
            var tr = Core.Translation.Translator.Current;
            foreach (var j in description.Jobs)
            {
                Jobs.Add(j with { Comment = tr.Translate(j.Comment) });
            }

            StatusText = $"{description.Name}: {Jobs.Count} jobs.";
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                StatusText = ex.Message;
            }
        }
        finally
        {
            // Only the newest load may clear the busy flag.
            if (!ct.IsCancellationRequested)
            {
                IsWorking = false;
                RunCommand.NotifyCanExecuteChanged();
            }
        }
    }

    partial void OnSelectedJobChanged(JobDescription? value)
    {
        JobDetails = null;
        if (value is null || LoadedName is null)
        {
            return;
        }

        _ = LoadJobAsync(value);
    }

    private async Task LoadJobAsync(JobDescription job)
    {
        try
        {
            var details = await _inspector!.DescribeJobAsync(VariantName, job.Name);
            if (ReferenceEquals(SelectedJob, job))
            {
                var tr = Core.Translation.Translator.Current;
                JobDetails = details with
                {
                    Comment = tr.Translate(details.Comment),
                    Arguments = details.Arguments.Select(a => a with { Comment = tr.Translate(a.Comment) }).ToList(),
                    Results = details.Results.Select(r => r with { Comment = tr.Translate(r.Comment) }).ToList(),
                };
            }
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private string VariantName => LoadedName?.Split('→').Last().Trim() ?? string.Empty;

    private bool CanRun() => SelectedJob is not null && _session.IsConnected && !IsWorking;

    [RelayCommand(CanExecute = nameof(CanRun), IncludeCancelCommand = true)]
    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var job = SelectedJob!;
        if (job.IsPotentiallyDestructive &&
            !_dialogs.Confirm("Run a writing job?",
                $"{job.Name} may change the control unit (coding, adaptation, actuator, clearing or flashing).\n\n" +
                "Only continue if you know exactly what it does. Run it with ignition on and a battery charger connected."))
        {
            return;
        }

        IsWorking = true;
        RunCommand.NotifyCanExecuteChanged();
        Results.Clear();
        ResultSummary = $"Running {job.Name}…";
        try
        {
            var r = await _session.RequireEngine().ExecuteAsync(VariantName, job.Name, Arguments.Trim(), cancellationToken);
            for (var i = 0; i < r.Sets.Count; i++)
            {
                foreach (var v in r.Sets[i].Ordered)
                {
                    Results.Add(new ResultRow(i + 1, v.Name, v.Display, v.KindLabel));
                }
            }

            ResultSummary = $"{r.Variant}.{job.Name}: {r.JobStatus ?? "no JOB_STATUS"} · {r.Sets.Count} set(s) · {r.Duration.TotalMilliseconds:0} ms";
            _session.Info(ResultSummary);
        }
        catch (DiagnosticException ex)
        {
            ResultSummary = ex.Message;
        }
        catch (OperationCanceledException)
        {
            ResultSummary = "Cancelled.";
        }
        finally
        {
            IsWorking = false;
            RunCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand]
    private void CopyResults()
    {
        var sb = new StringBuilder();
        sb.AppendLine(ResultSummary);
        foreach (var r in Results)
        {
            sb.Append(r.Set).Append('\t').Append(r.Name).Append('\t').Append(r.Value).Append('\t').AppendLine(r.Type);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            ResultSummary = "Clipboard is busy – try again.";
        }
    }

    private static bool Matches(string? text, string filter) =>
        string.IsNullOrWhiteSpace(filter) || (text?.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase) ?? false);
}

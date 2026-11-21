using BmwDecode.Core.Diagnostics;

namespace BmwDecode.Core.Services;

/// <summary>One polling round: values of all selected jobs plus any per-job errors.</summary>
public sealed record LiveSample(DateTimeOffset Time, IReadOnlyList<LiveValue> Values, IReadOnlyDictionary<string, string> Errors);

/// <summary>Repeatedly runs STATUS_* jobs of one ECU and yields their values.</summary>
public sealed class LiveDataPoller
{
    private readonly IDiagnosticEngine _engine;

    public LiveDataPoller(IDiagnosticEngine engine) => _engine = engine;

    public async IAsyncEnumerable<LiveSample> PollAsync(
        string variant,
        IReadOnlyList<string> jobs,
        TimeSpan interval,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var started = DateTimeOffset.Now;
            var values = new List<LiveValue>();
            var errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var job in jobs)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    yield break;
                }

                try
                {
                    var r = await _engine.ExecuteAsync(variant, job, "", cancellationToken).ConfigureAwait(false);
                    values.AddRange(FaultParser.ParseLive(r));
                    if (!r.IsOkay && r.JobStatus is { } status)
                    {
                        errors[job] = status;
                    }
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
                catch (DiagnosticException ex)
                {
                    errors[job] = ex.Message;
                }
            }

            yield return new LiveSample(DateTimeOffset.Now, values, errors);

            var wait = interval - (DateTimeOffset.Now - started);
            if (wait > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(wait, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    yield break;
                }
            }
        }
    }
}

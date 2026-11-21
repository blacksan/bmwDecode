using BmwDecode.Core.Catalog;
using BmwDecode.Core.Diagnostics;
using BmwDecode.Core.Services;
using BmwDecode.Core.Simulation;

namespace BmwDecode.Core.Tests;

public class ServiceTests
{
    private static async Task<SimulatedEngine> ConnectedDemoAsync()
    {
        var engine = new SimulatedEngine(fast: true);
        await engine.ConnectAsync(new ConnectionSettings { Kind = InterfaceKind.Demo });
        return engine;
    }

    [Fact]
    public async Task Vehicle_identification_reads_vin_and_series_from_the_cas()
    {
        await using var engine = await ConnectedDemoAsync();

        var id = await new VehicleIdentifier(engine).IdentifyAsync();

        Assert.Equal(SimulatedEngine.DemoVin, id.Vin);
        Assert.Equal("E84", id.Series);
        Assert.Equal("0E84X10", id.ShortVin);
        Assert.InRange(id.BatteryMillivolts!.Value, 12_000L, 13_000L);
        Assert.True(id.IgnitionOn);
        Assert.StartsWith("CAS", id.VinSource);
    }

    [Fact]
    public async Task Quick_test_finds_fitted_modules_and_their_faults()
    {
        await using var engine = await ConnectedDemoAsync();
        var series = VehicleCatalog.Default.Find("E84")!;
        var progress = new List<EcuScanResult>();

        var report = await new QuickTestService(engine).RunAsync(series, VehicleIdentity.Unknown, new QuickTestOptions(),
            new SyncProgress<EcuScanResult>(progress.Add));

        var dde = report.Results.Single(r => r.Ecu.PrimaryGroup == "D_MOTOR");
        Assert.Equal("D60M57A0", dde.Variant);
        Assert.Equal(EcuScanState.Faults, dde.State);
        Assert.Equal(3, dde.FaultList.Count);
        Assert.Equal("7 811 571", dde.PartNumber);

        // The demo car is a RWD conversion: no transfer case.
        Assert.Equal(EcuScanState.NotFitted, report.Results.Single(r => r.Ecu.PrimaryGroup == "D_VGSG").State);
        Assert.Equal(EcuScanState.NoFaults, report.Results.Single(r => r.Ecu.PrimaryGroup == "D_KLIMA").State);
        Assert.False(report.WasCancelled);
        Assert.Equal(8, report.FaultCount);
        Assert.Contains(progress, p => p.State == EcuScanState.Scanning);
    }

    [Fact]
    public async Task Clearing_faults_removes_stored_but_not_active_faults()
    {
        await using var engine = await ConnectedDemoAsync();
        var quick = new QuickTestService(engine);
        var series = VehicleCatalog.Default.Find("E84")!;
        var options = new QuickTestOptions { ReadIdent = false };

        var before = await quick.RunAsync(series, VehicleIdentity.Unknown, options);
        var cleared = await quick.ClearAllAsync(before.Results);
        var after = await quick.RunAsync(series, VehicleIdentity.Unknown, options);

        Assert.All(cleared, c => Assert.True(c.Ok));
        Assert.True(after.FaultCount < before.FaultCount);
        Assert.All(after.Results.SelectMany(r => r.FaultList), f => Assert.True(f.IsPresent));
    }

    [Fact]
    public async Task Quick_test_honours_cancellation()
    {
        await using var engine = await ConnectedDemoAsync();
        using var cts = new CancellationTokenSource();
        var series = VehicleCatalog.Default.Find("E84")!;
        var seen = 0;

        var report = await new QuickTestService(engine).RunAsync(series, VehicleIdentity.Unknown, new QuickTestOptions(),
            new SyncProgress<EcuScanResult>(r =>
            {
                if (r.State != EcuScanState.Pending && r.State != EcuScanState.Scanning && ++seen == 3)
                {
                    cts.Cancel();
                }
            }),
            cts.Token);

        Assert.True(report.WasCancelled);
        Assert.True(report.Results.Count < series.Ecus.Count);
    }

    [Fact]
    public async Task Live_data_poller_yields_values_with_units()
    {
        await using var engine = await ConnectedDemoAsync();
        using var cts = new CancellationTokenSource();
        var samples = new List<LiveSample>();

        await foreach (var sample in new LiveDataPoller(engine).PollAsync("D60M57A0", ["STATUS_MOTORDREHZAHL", "STATUS_UBATT", "STATUS_NICHT_DA"], TimeSpan.Zero, cts.Token))
        {
            samples.Add(sample);
            if (samples.Count == 2)
            {
                cts.Cancel();
            }
        }

        Assert.Equal(2, samples.Count);
        var rpm = samples[0].Values.Single(v => v.Key == "STAT_MOTORDREHZAHL_WERT");
        Assert.Equal("1/min", rpm.Unit);
        Assert.InRange(rpm.Numeric!.Value, 700, 900);
        Assert.True(samples[0].Errors.ContainsKey("STATUS_NICHT_DA"));
    }

    [Fact]
    public async Task Reports_contain_vehicle_modules_and_faults()
    {
        await using var engine = await ConnectedDemoAsync();
        var id = await new VehicleIdentifier(engine).IdentifyAsync();
        var report = await new QuickTestService(engine).RunAsync(VehicleCatalog.Default.Find("E84")!, id, new QuickTestOptions());

        var html = ReportExporter.ToHtml(report);
        var csv = ReportExporter.ToCsv(report);

        Assert.Contains(SimulatedEngine.DemoVin, html);
        Assert.Contains("4B93", html);
        Assert.Contains("D60M57A0", html);
        Assert.StartsWith("address,module", csv);
        Assert.Contains("0x12,DME/DDE", csv);
    }

    [Fact]
    public void Vin_and_series_helpers_reject_garbage()
    {
        Assert.Equal("WBAVN31000VL12345", VehicleIdentifier.NormalizeVin(" wbavn31000vl12345 "));
        Assert.Null(VehicleIdentifier.NormalizeVin("00000000000000000"));
        Assert.Null(VehicleIdentifier.NormalizeVin("WBAVN31000VL1234O")); // letter O is not allowed in a VIN
        Assert.Equal("E84", VehicleIdentifier.SeriesFromOrder("E84_#0909%0730&ZDCC$205*"));
        Assert.Null(VehicleIdentifier.SeriesFromOrder("garbage"));
        Assert.Equal("E90", VehicleIdentifier.NormalizeSeries("E90 "));
    }

    [Fact]
    public void Read_only_adaptation_jobs_are_not_flagged()
    {
        Assert.False(JobSafety.IsPotentiallyDestructive("ABGLEICH_LESEN"));
        Assert.True(JobSafety.IsPotentiallyDestructive("ABGLEICH_PROG"));
    }

    /// <summary>IProgress that reports synchronously (Progress&lt;T&gt; posts to the thread pool).</summary>
    private sealed class SyncProgress<T>(Action<T> action) : IProgress<T>
    {
        public void Report(T value) => action(value);
    }
}

using System.Windows;
using System.Windows.Threading;

namespace BmwDecode.App.Services;

/// <summary>The WPF dispatcher behind <see cref="IUiThread"/>.</summary>
public sealed class WpfUiThread : IUiThread
{
    private static Dispatcher? Dispatcher => Application.Current?.Dispatcher;

    public bool CheckAccess() => Dispatcher is not { } d || d.CheckAccess();

    public void Invoke(Action action)
    {
        if (Dispatcher is { } d && !d.CheckAccess())
        {
            d.Invoke(action);
        }
        else
        {
            action();
        }
    }

    public void Post(Action action)
    {
        if (Dispatcher is { } d)
        {
            d.BeginInvoke(action);
        }
        else
        {
            action();
        }
    }
}

public sealed class WpfClipboard : IClipboardService
{
    public Task<bool> TrySetTextAsync(string text)
    {
        try
        {
            Clipboard.SetText(text);
            return Task.FromResult(true);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return Task.FromResult(false);
        }
    }
}

public sealed class WpfTimerFactory : IUiTimerFactory
{
    public IUiTimer Create(TimeSpan interval) => new Timer(interval);

    private sealed class Timer : IUiTimer
    {
        private readonly DispatcherTimer _timer;

        public Timer(TimeSpan interval)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (s, e) => Tick?.Invoke(this, e);
        }

        public event EventHandler? Tick;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();
    }
}

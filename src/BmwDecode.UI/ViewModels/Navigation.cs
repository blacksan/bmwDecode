using CommunityToolkit.Mvvm.ComponentModel;

namespace BmwDecode.App.ViewModels;

/// <summary>Base class for pages shown in the main window's content area.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    protected PageViewModel(string title, string glyph)
    {
        Title = title;
        Glyph = glyph;
    }

    public string Title { get; }

    /// <summary>Segoe Fluent Icons glyph for the navigation pane.</summary>
    public string Glyph { get; }

    [ObservableProperty]
    private string? _statusText;

    [ObservableProperty]
    private bool _isWorking;

    /// <summary>Called every time the page becomes visible.</summary>
    public virtual void OnNavigatedTo()
    {
    }
}

/// <summary>Holds the current page; pages use it to jump to each other.</summary>
public sealed partial class Navigator : ObservableObject
{
    private readonly Dictionary<Type, PageViewModel> _pages = [];

    [ObservableProperty]
    private PageViewModel? _current;

    public void Register(PageViewModel page) => _pages[page.GetType()] = page;

    public T Get<T>() where T : PageViewModel => (T)_pages[typeof(T)];

    public T NavigateTo<T>() where T : PageViewModel
    {
        var page = Get<T>();
        Current = page;
        return page;
    }

    partial void OnCurrentChanged(PageViewModel? value) => value?.OnNavigatedTo();
}

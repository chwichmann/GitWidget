using CommunityToolkit.Mvvm.ComponentModel;
using GitStatusWidget.Domain.Events;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>Semantic kind of a toast (drives color and icon; never color alone).</summary>
public enum ToastKind
{
    Info,
    Success,
    Failure,
    Warning,
}

/// <summary>Content of a short-lived notification.</summary>
public sealed partial class ToastViewModel : ObservableObject
{
    public ToastViewModel(string repositoryName, string headline, string title, ToastKind kind, Uri? url)
    {
        RepositoryName = repositoryName;
        Headline = headline;
        Title = title;
        Kind = kind;
        Url = url;
    }

    public string RepositoryName { get; }

    public string Headline { get; }

    public string Title { get; }

    public ToastKind Kind { get; }

    public Uri? Url { get; }

    public string Glyph => Kind switch
    {
        ToastKind.Success => "✓",
        ToastKind.Failure => "✕",
        ToastKind.Warning => "!",
        _ => "+",
    };

    public static ToastKind KindOf(ChangeEventType type) => type switch
    {
        ChangeEventType.IssueClosed or ChangeEventType.ChangeRequestMerged or ChangeEventType.BuildSucceeded or ChangeEventType.MilestoneClosed => ToastKind.Success,
        ChangeEventType.BuildFailed => ToastKind.Failure,
        ChangeEventType.BuildCanceled => ToastKind.Warning,
        _ => ToastKind.Info,
    };
}

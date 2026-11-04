using System.Runtime.CompilerServices;
using GitStatusWidget.Desktop.ViewModels;

namespace GitStatusWidget.Desktop.Tests.TestDoubles;

/// <summary>
/// Production bugs that block whole groups of tests. Affected tests are skipped only while the bug is present and
/// run automatically once it is fixed.
/// </summary>
public static class KnownBugs
{
    public const string EditorConstructor =
        "Bug: RepositoryEditorViewModel constructor throws NullReferenceException (Provider is assigned before ServerUrl, " +
        "OnProviderChanged dereferences the null ServerUrl), so Settings cannot open; see report.";

    public const string RunWriteBack =
        "Bug: Run.Text is TwoWay by default, so dashboard Runs with one-way converters try ConvertBack and log binding " +
        "warnings (DashboardWindow.axaml); see report.";

    private static readonly Lazy<bool> EditorThrows = new(() =>
    {
        try
        {
            _ = new RepositoryEditorViewModel(null, hasStoredToken: false);
            return false;
        }
        catch (NullReferenceException)
        {
            return true;
        }
    });

    /// <summary>Whether constructing a <see cref="RepositoryEditorViewModel"/> still throws.</summary>
    public static bool EditorConstructorThrows => EditorThrows.Value;

    /// <summary>Whether a captured binding message is the known <see cref="RunWriteBack"/> warning.</summary>
    public static bool IsRunWriteBack(string message) =>
        message.Contains("Run:", StringComparison.Ordinal) && message.Contains("Specified method is not supported", StringComparison.Ordinal);
}

/// <summary>A fact that needs <see cref="RepositoryEditorViewModel"/>; skipped while <see cref="KnownBugs.EditorConstructor"/> is present.</summary>
public sealed class EditorFactAttribute : FactAttribute
{
    public EditorFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = KnownBugs.EditorConstructor;
        SkipType = typeof(KnownBugs);
        SkipWhen = nameof(KnownBugs.EditorConstructorThrows);
    }
}

/// <summary>A theory that needs <see cref="RepositoryEditorViewModel"/>; skipped while <see cref="KnownBugs.EditorConstructor"/> is present.</summary>
public sealed class EditorTheoryAttribute : TheoryAttribute
{
    public EditorTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = KnownBugs.EditorConstructor;
        SkipType = typeof(KnownBugs);
        SkipWhen = nameof(KnownBugs.EditorConstructorThrows);
    }
}

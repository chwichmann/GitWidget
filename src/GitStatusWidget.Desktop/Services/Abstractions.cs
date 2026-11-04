namespace GitStatusWidget.Desktop.Services;

/// <summary>Posts work to the UI thread (abstracted for view-model tests).</summary>
public interface IUiDispatcher
{
    void Post(Action action);
}

/// <summary>Window navigation, implemented by <see cref="WindowManager"/>.</summary>
public interface IAppNavigator
{
    void ShowWidget(bool activate = true);

    void HideWidget();

    void ToggleWidget();

    void ShowDashboard(Guid? repositoryId = null);

    void ShowSettings(Guid? repositoryId = null, bool addNew = false);

    void ShowDiagnostics();

    void OpenInBrowser(Uri? uri);

    void ExitApplication();
}

/// <summary>Plays one-shot animations on the widget (decoupled from view models).</summary>
public enum RowAnimation
{
    IssueClosed,
    IssueOpened,
    BuildFailed,
}

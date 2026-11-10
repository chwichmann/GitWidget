using GitStatusWidget.Application.Projection;
using GitStatusWidget.Domain.Builds;
using GitStatusWidget.Domain.Health;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.ViewModels;

/// <summary>
/// Sample rows shown in the widget while no repository is configured, so the widget and its settings
/// (for example the transparency) can be tried before the first repository is added.
/// </summary>
internal static class DemoData
{
    private static readonly Guid GitHubId = Guid.Parse("d3300000-0000-4000-8000-000000000001");
    private static readonly Guid GitLabId = Guid.Parse("d3300000-0000-4000-8000-000000000002");

    /// <summary>Whether a row id belongs to the demo rows.</summary>
    public static bool IsDemo(Guid id) => id == GitHubId || id == GitLabId;

    /// <summary>Builds the demo rows.</summary>
    /// <param name="now">Current time (UTC).</param>
    /// <param name="periodLabel">Label of the counting period, as in <see cref="SummaryProjection.PeriodLabel"/>.</param>
    public static IReadOnlyList<CompactRowProjection> Rows(DateTimeOffset now, string periodLabel)
    {
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var example = new Uri("https://example.invalid/");
        return
        [
            new CompactRowProjection(
                new RepositoryHeaderProjection(GitHubId, "Demo: website", "demo/website", RepositoryProviderKind.GitHub, "github.com", "main", null),
                true,
                new SummaryProjection(12, 48, 3, 2, 4, 5, 2, "pull requests", periodLabel, 5),
                [
                    new MilestoneProgressProjection("demo-1", "1.0", 3, 7, today.AddDays(12), false, false, null) { IsInWidget = true },
                    new MilestoneProgressProjection("demo-2", "1.1", 6, 2, null, false, false, null) { IsInWidget = true },
                ],
                new BuildProjection("CI", BuildState.Succeeded, "main", "0a1b2c3d", now.AddMinutes(-25), now.AddMinutes(-20), example),
                Health(now)),
            new CompactRowProjection(
                new RepositoryHeaderProjection(GitLabId, "Demo: backend", "demo/group/backend", RepositoryProviderKind.GitLab, "gitlab.example.com", "main", null),
                true,
                new SummaryProjection(27, 310, 1, 0, 9, 11, 3, "merge requests", periodLabel, 0),
                [new MilestoneProgressProjection("demo-3", "Sprint 42", 9, 4, today.AddDays(-2), false, true, null) { IsInWidget = true }],
                new BuildProjection("Pipeline", BuildState.Failed, "main", "4e5f6a7b", now.AddMinutes(-70), now.AddMinutes(-62), example),
                Health(now)),
        ];
    }

    private static RepositoryHealthProjection Health(DateTimeOffset now) =>
        new(RepositoryHealthState.Healthy, "Demo data", null, now, now, null, false, null);
}

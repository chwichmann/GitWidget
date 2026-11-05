using GitStatusWidget.Application.Settings;
using GitStatusWidget.Application.Synchronization;
using GitStatusWidget.Application.Updates;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace GitStatusWidget.Application.Tests.Updates;

public sealed class UpdateCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);

    private static ReleaseInfo Release(string tag, bool prerelease = false) =>
        new(SemanticVersion.TryParse(tag, out var v) ? v : throw new ArgumentException(tag), tag, new Uri($"https://github.com/chwichmann/GitWidget/releases/tag/{tag}"), prerelease, Now);

    [Theory]
    [InlineData("0.1.2", "0.1.1", 1)]
    [InlineData("v1.0.0", "0.9.9", 1)]
    [InlineData("1.0.0", "1.0.0-rc.1", 1)]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.10", -1)]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1", -1)]
    [InlineData("0.1.1-ci.57", "0.1.1", -1)]
    [InlineData("0.2", "0.2.0", 0)]
    [InlineData("0.1.1+abc", "0.1.1", 0)]
    public void Semantic_versions_compare_with_semver_precedence(string a, string b, int expected)
    {
        SemanticVersion.TryParse(a, out var x).Should().BeTrue();
        SemanticVersion.TryParse(b, out var y).Should().BeTrue();

        Math.Sign(x.CompareTo(y)).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    public void Non_versions_are_rejected(string text) => SemanticVersion.TryParse(text, out _).Should().BeFalse();

    [Fact]
    public void Newest_offers_only_newer_releases_and_respects_the_prerelease_setting()
    {
        SemanticVersion.TryParse("0.1.1", out var current).Should().BeTrue();
        ReleaseInfo[] releases = [Release("v0.1.0"), Release("v0.1.1"), Release("v0.1.2", prerelease: true), Release("v0.2.0-beta.1")];

        UpdateChecker.Newest(releases, current, includePrereleases: true)!.Tag.Should().Be("v0.2.0-beta.1");
        UpdateChecker.Newest(releases, current, includePrereleases: false).Should().BeNull();
        UpdateChecker.Newest([Release("v0.1.1")], current, includePrereleases: true).Should().BeNull();
    }

    [Fact]
    public async Task Check_reports_an_available_update_once()
    {
        var feed = new FakeFeed(new ReleaseFeedResult.Success([Release("v0.1.2", prerelease: true)]));
        var checker = Create(feed, new AppSettings());
        var raised = new List<UpdateState>();
        checker.StateChanged += (_, s) => raised.Add(s);

        await checker.CheckAsync(CancellationToken.None);
        await checker.CheckAsync(CancellationToken.None);

        checker.State.Status.Should().Be(UpdateStatus.Available);
        checker.State.Latest!.PageUrl.ToString().Should().EndWith("/releases/tag/v0.1.2");
        checker.State.CurrentVersion.Should().Be("0.1.1");
        raised.Should().ContainSingle();
    }

    [Fact]
    public async Task Up_to_date_unavailable_and_disabled_are_distinguished()
    {
        var upToDate = Create(new FakeFeed(new ReleaseFeedResult.Success([Release("v0.1.1")])), new AppSettings());
        (await upToDate.CheckAsync(CancellationToken.None)).Status.Should().Be(UpdateStatus.UpToDate);

        var unavailable = Create(new FakeFeed(new ReleaseFeedResult.Unavailable("private repository")), new AppSettings());
        var state = await unavailable.CheckAsync(CancellationToken.None);
        state.Status.Should().Be(UpdateStatus.Unavailable);
        state.Message.Should().Be("private repository");

        var feed = new FakeFeed(new ReleaseFeedResult.Success([Release("v9.0.0")]));
        var disabled = Create(feed, new AppSettings { CheckForUpdates = false });
        (await disabled.CheckAsync(CancellationToken.None)).Status.Should().Be(UpdateStatus.Unknown);
        feed.Calls.Should().Be(0, "no request is made when update checks are disabled");
    }

    [Fact]
    public async Task First_automatic_check_waits_for_the_initial_delay()
    {
        var time = new FakeTimeProvider(Now);
        var feed = new FakeFeed(new ReleaseFeedResult.Success([Release("v0.1.2")]));
        using var checker = new UpdateChecker(feed, new Controller(new AppSettings()), time, NullLogger<UpdateChecker>.Instance, "0.1.1", TimeSpan.FromMinutes(2));
        using var cts = new CancellationTokenSource();

        await checker.StartAsync(cts.Token);
        await Task.Delay(50);
        feed.Calls.Should().Be(0);

        time.Advance(TimeSpan.FromMinutes(2));
        for (var i = 0; i < 100 && feed.Calls == 0; i++)
        {
            await Task.Delay(10);
        }

        feed.Calls.Should().Be(1);
        await cts.CancelAsync();
        await checker.StopAsync(CancellationToken.None);
    }

    private static UpdateChecker Create(IReleaseFeed feed, AppSettings settings) =>
        new(feed, new Controller(settings), new FakeTimeProvider(Now), NullLogger<UpdateChecker>.Instance, "0.1.1", TimeSpan.Zero);

    private sealed class FakeFeed(ReleaseFeedResult result) : IReleaseFeed
    {
        public int Calls { get; private set; }

        public string Source => "chwichmann/GitWidget";

        public Task<ReleaseFeedResult> GetReleasesAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(result);
        }
    }

    private sealed class Controller(AppSettings settings) : ISynchronizationController
    {
        public AppSettings Settings { get; } = settings;

        public void RequestRefresh(Guid? repositoryId = null)
        {
        }

        public Task ApplySettingsAsync(AppSettings settings, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

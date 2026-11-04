using GitStatusWidget.Infrastructure.Platform;

namespace GitStatusWidget.Infrastructure.Tests.Platform;

public sealed class UrlLauncherTests
{
    [Theory]
    [InlineData("https://github.com/owner/repo/issues/1")]
    [InlineData("https://gitlab.example.com/group/sub/project/-/issues/7?x=1#note_2")]
    [InlineData("https://xn--bcher-kva.example/path")]
    public void Accepts_https(string url)
    {
        UrlLauncher.TryGetLaunchableUrl(new Uri(url), allowHttp: false, out string? launchable).Should().BeTrue();
        launchable.Should().StartWith("https://");
    }

    [Fact]
    public void Http_only_when_allowed()
    {
        var uri = new Uri("http://gitlab.internal/group/project");

        UrlLauncher.TryGetLaunchableUrl(uri, allowHttp: false, out _).Should().BeFalse();
        UrlLauncher.TryGetLaunchableUrl(uri, allowHttp: true, out string? launchable).Should().BeTrue();
        launchable.Should().Be("http://gitlab.internal/group/project");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]
    [InlineData("file:///etc/passwd")]
    [InlineData("file://server/share/evil.exe")]
    [InlineData("ftp://example.com/x")]
    [InlineData("mailto:someone@example.com")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("ms-settings:privacy")]
    [InlineData("vscode://file/x")]
    [InlineData("https://user:password@github.com/")]
    [InlineData("https://token@github.com/")]
    public void Rejects_dangerous_or_credentialed_urls(string url)
    {
        UrlLauncher.TryGetLaunchableUrl(new Uri(url), allowHttp: true, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("https://github.com/a b")]
    [InlineData("https://github.com/a\tb")]
    [InlineData("https://github.com/a\nb")]
    [InlineData("https://github.com/a\u0007b")]
    [InlineData("https://github.com/a‮b")]
    [InlineData(" https://github.com/")]
    public void Rejects_whitespace_and_control_characters(string url)
    {
        Uri.TryCreate(url, UriKind.Absolute, out Uri? uri);

        UrlLauncher.TryGetLaunchableUrl(uri, allowHttp: true, out _).Should().BeFalse();
    }

    [Fact]
    public void Rejects_relative_null_and_oversized()
    {
        UrlLauncher.TryGetLaunchableUrl(null, allowHttp: true, out _).Should().BeFalse();
        UrlLauncher.TryGetLaunchableUrl(new Uri("/relative/path", UriKind.Relative), allowHttp: true, out _).Should().BeFalse();
        UrlLauncher.TryGetLaunchableUrl(new Uri("https://github.com/" + new string('a', UrlLauncher.MaxUrlLength)), allowHttp: false, out _).Should().BeFalse();
    }

    [Fact]
    public void Launcher_refuses_invalid_url_without_starting_a_process()
    {
        new UrlLauncher().TryOpen(new Uri("javascript:alert(1)"), allowHttp: false).Should().BeFalse();
    }

    [Fact]
    public void Start_info_passes_url_as_single_argument_without_shell()
    {
        const string url = "https://github.com/o/r/issues?q=a%26b%7Cc";

        var startInfo = UrlLauncher.CreateStartInfo(url)!;

        if (OperatingSystem.IsWindows())
        {
            startInfo.FileName.Should().Be(url);
            startInfo.UseShellExecute.Should().BeTrue();
            startInfo.Arguments.Should().BeEmpty();
        }
        else
        {
            startInfo.FileName.Should().Be(OperatingSystem.IsMacOS() ? "/usr/bin/open" : "xdg-open");
            startInfo.UseShellExecute.Should().BeFalse();
            startInfo.ArgumentList.Should().Equal(url);
            startInfo.Arguments.Should().BeEmpty();
        }
    }
}

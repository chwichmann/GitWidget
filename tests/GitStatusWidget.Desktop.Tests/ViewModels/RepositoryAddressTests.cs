using GitStatusWidget.Desktop.ViewModels;
using GitStatusWidget.Domain.Repositories;

namespace GitStatusWidget.Desktop.Tests.ViewModels;

public sealed class RepositoryAddressTests
{
    [Theory]
    [InlineData("https://github.com/octo/widget")]
    [InlineData("https://github.com/octo/widget/")]
    [InlineData("https://github.com/octo/widget.git")]
    [InlineData("https://github.com/octo/widget/issues")]
    [InlineData("https://github.com/octo/widget/issues/42")]
    [InlineData("https://github.com/octo/widget/pull/7/files")]
    [InlineData("https://github.com/octo/widget?tab=readme#install")]
    [InlineData("  https://GitHub.com/octo/widget  ")]
    public void GitHub_urls_are_reduced_to_owner_and_name(string input)
    {
        var ok = RepositoryAddress.TryParseUrl(input, null, out var server, out var path, out var provider);

        ok.Should().BeTrue();
        server.Should().Be(new Uri("https://github.com/"));
        path.Should().Be("octo/widget");
        provider.Should().Be(RepositoryProviderKind.GitHub);
    }

    [Theory]
    [InlineData("https://gitlab.com/group/sub/project")]
    [InlineData("https://gitlab.com/group/sub/project.git")]
    [InlineData("https://gitlab.com/group/sub/project/-/issues")]
    [InlineData("https://gitlab.com/group/sub/project/-/work_items/5")]
    [InlineData("https://gitlab.com/group/sub/project/-/merge_requests/12/diffs")]
    public void GitLab_subgroup_urls_keep_the_full_namespace(string input)
    {
        var ok = RepositoryAddress.TryParseUrl(input, null, out var server, out var path, out var provider);

        ok.Should().BeTrue();
        server.Should().Be(new Uri("https://gitlab.com/"));
        path.Should().Be("group/sub/project");
        provider.Should().Be(RepositoryProviderKind.GitLab);
    }

    [Fact]
    public void Self_hosted_gitlab_host_is_detected_by_name()
    {
        RepositoryAddress.TryParseUrl("https://gitlab.example.com/team/app/-/pipelines", null, out var server, out var path, out var provider)
            .Should().BeTrue();

        server.Should().Be(new Uri("https://gitlab.example.com/"));
        path.Should().Be("team/app");
        provider.Should().Be(RepositoryProviderKind.GitLab);
    }

    [Fact]
    public void Known_server_with_relative_root_is_kept_as_server()
    {
        var known = new Uri("https://code.example.com/gitlab/");

        var ok = RepositoryAddress.TryParseUrl("https://code.example.com/gitlab/team/sub/app/-/issues", known, out var server, out var path, out var provider);

        ok.Should().BeTrue();
        server.Should().Be(known);
        path.Should().Be("team/sub/app");
        provider.Should().BeNull("the host name does not identify the provider");
    }

    [Fact]
    public void Without_known_server_a_relative_root_is_treated_as_a_group()
    {
        var ok = RepositoryAddress.TryParseUrl("https://code.example.com/gitlab/team/app", null, out var server, out var path, out _);

        ok.Should().BeTrue();
        server.Should().Be(new Uri("https://code.example.com/"));
        path.Should().Be("gitlab/team/app");
    }

    [Fact]
    public void Known_server_that_is_not_a_prefix_is_ignored()
    {
        var ok = RepositoryAddress.TryParseUrl("https://gitlab.com/group/project", new Uri("https://github.com/"), out var server, out var path, out var provider);

        ok.Should().BeTrue();
        server.Should().Be(new Uri("https://gitlab.com/"));
        path.Should().Be("group/project");
        provider.Should().Be(RepositoryProviderKind.GitLab);
    }

    [Fact]
    public void Plain_http_urls_are_parsed_with_their_scheme()
    {
        var ok = RepositoryAddress.TryParseUrl("http://gitlab.internal:8080/group/project", null, out var server, out var path, out var provider);

        ok.Should().BeTrue();
        server.Should().Be(new Uri("http://gitlab.internal:8080/"));
        server.Scheme.Should().Be(Uri.UriSchemeHttp);
        path.Should().Be("group/project");
        provider.Should().Be(RepositoryProviderKind.GitLab);
    }

    [Fact]
    public void Https_urls_keep_https()
    {
        RepositoryAddress.TryParseUrl("https://gitlab.internal/group/project", null, out var server, out _, out _).Should().BeTrue();

        server.Scheme.Should().Be(Uri.UriSchemeHttps);
    }

    [Theory]
    [InlineData("octo/widget")]
    [InlineData("")]
    [InlineData("not a url")]
    [InlineData("ftp://github.com/octo/widget")]
    [InlineData("git@github.com:octo/widget.git")]
    [InlineData("file:///home/user/repo")]
    public void Non_web_urls_are_rejected(string input) =>
        RepositoryAddress.TryParseUrl(input, null, out _, out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("https://github.com/")]
    [InlineData("https://github.com/octo")]
    [InlineData("https://gitlab.com/group")]
    public void Urls_without_owner_and_name_are_rejected(string input) =>
        RepositoryAddress.TryParseUrl(input, null, out _, out _, out _).Should().BeFalse();

    [Fact]
    public void Escaped_path_segments_are_unescaped()
    {
        RepositoryAddress.TryParseUrl("https://gitlab.com/my%20group/project", null, out _, out var path, out _).Should().BeTrue();

        path.Should().Be("my group/project");
    }

    [Fact]
    public void User_info_is_ignored()
    {
        var ok = RepositoryAddress.TryParseUrl("https://oauth2@gitlab.com/group/project.git", null, out var server, out var path, out _);

        ok.Should().BeTrue();
        server.Should().Be(new Uri("https://gitlab.com/"));
        path.Should().Be("group/project");
    }

    [Theory]
    [InlineData("octo/widget", "octo", "widget")]
    [InlineData(" /octo/widget/ ", "octo", "widget")]
    [InlineData("group/sub/project", "group/sub", "project")]
    [InlineData("widget", "", "widget")]
    [InlineData("/widget", "", "widget")]
    [InlineData("", "", "")]
    public void Split_separates_owner_path_and_name(string input, string owner, string name) =>
        RepositoryAddress.Split(input).Should().Be((owner, name));
}

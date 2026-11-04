using GitStatusWidget.Infrastructure.Providers.Http;

namespace GitStatusWidget.Infrastructure.Tests.Providers.Http;

public sealed class LinkHeaderTests
{
    [Fact]
    public void Parses_next_and_last_relations()
    {
        const string header = "<https://api.github.com/repositories/1/issues?per_page=100&after=Y3Vy>; rel=\"next\", <https://api.github.com/x?page=9>; rel=\"last\"";

        LinkHeader.GetRelation(header, "next")!.Query.Should().Contain("after=Y3Vy");
        LinkHeader.GetRelation(header, "last")!.Query.Should().Be("?page=9");
        LinkHeader.GetRelation(header, "prev").Should().BeNull();
    }

    [Fact]
    public void Tolerates_missing_or_garbage_headers()
    {
        LinkHeader.GetRelation(null, "next").Should().BeNull();
        LinkHeader.GetRelation("garbage", "next").Should().BeNull();
    }

    [Fact]
    public void Url_redaction_hides_token_parameters_and_user_info()
    {
        var redacted = UrlRedaction.Redact(new Uri("https://user:pw@gitlab.example.com/api/v4/projects?private_token=secret&per_page=1"));

        redacted.Should().NotContain("secret").And.NotContain("pw").And.Contain("per_page=1");
    }
}

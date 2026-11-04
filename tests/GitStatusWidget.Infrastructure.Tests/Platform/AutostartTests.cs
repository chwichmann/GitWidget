using System.Xml.Linq;
using GitStatusWidget.Infrastructure.Platform;

namespace GitStatusWidget.Infrastructure.Tests.Platform;

public sealed class AutostartTests : IDisposable
{
    private const string SpacedExe = "/Users/me/OneDrive/Visual Studio Projects/GitWidget/GitWidget.app/Contents/MacOS/GitWidget";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "GitWidgetTest-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact]
    public void Resolves_published_executable()
    {
        string exe = Path.Combine(_root, "Visual Studio Projects", "GitWidget");

        var command = AutostartCommand.Resolve(exe, Path.Combine(_root, "GitWidget.dll"), _ => true);

        command!.Executable.Should().Be(exe);
        command.Arguments.Should().Equal("--autostart");
    }

    [Fact]
    public void Resolves_dotnet_host_plus_entry_assembly()
    {
        string host = Path.Combine(_root, "dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        string dll = Path.Combine(_root, "Visual Studio Projects", "GitWidget.dll");

        var command = AutostartCommand.Resolve(host, dll, path => path == dll);

        command!.Executable.Should().Be(host);
        command.Arguments.Should().Equal(dll, "--autostart");
    }

    [Fact]
    public void Unresolvable_command_is_null()
    {
        AutostartCommand.Resolve(null, null, _ => true).Should().BeNull();
        AutostartCommand.Resolve("relative/GitWidget", null, _ => true).Should().BeNull();
        string host = Path.Combine(_root, "dotnet");
        AutostartCommand.Resolve(host, Path.Combine(_root, "missing.dll"), _ => false).Should().BeNull();
    }

    [Fact]
    public void Windows_command_line_quotes_paths_with_spaces()
    {
        var command = new AutostartCommand(@"C:\Users\me\OneDrive\Visual Studio Projects\GitWidget\GitWidget.exe", ["--autostart"]);

        AutostartFormats.WindowsCommandLine(command)
            .Should().Be("\"C:\\Users\\me\\OneDrive\\Visual Studio Projects\\GitWidget\\GitWidget.exe\" --autostart");
    }

    [Fact]
    public void Windows_command_line_quotes_dotnet_host_and_dll()
    {
        var command = new AutostartCommand(@"C:\Program Files\dotnet\dotnet.exe", [@"C:\Visual Studio Projects\GitWidget.dll", "--autostart"]);

        AutostartFormats.WindowsCommandLine(command)
            .Should().Be("\"C:\\Program Files\\dotnet\\dotnet.exe\" \"C:\\Visual Studio Projects\\GitWidget.dll\" --autostart");
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("", "\"\"")]
    [InlineData("a b", "\"a b\"")]
    [InlineData("say \"hi\"", "\"say \\\"hi\\\"\"")]
    [InlineData(@"C:\dir with space\", "\"C:\\dir with space\\\\\"")]
    [InlineData(@"no\space", @"no\space")]
    public void Windows_argument_quoting_follows_CommandLineToArgvW(string argument, string expected)
    {
        AutostartFormats.QuoteWindowsArgument(argument).Should().Be(expected);
    }

    [Fact]
    public void Plist_contains_program_arguments_escaped_and_well_formed()
    {
        var command = new AutostartCommand("/usr/local/share/dotnet/dotnet", ["/Users/me/Visual Studio Projects/R&D <x>/GitWidget.dll", "--autostart"]);

        string plist = AutostartFormats.LaunchAgentPlist("io.github.chwichmann.GitStatusWidget", command);

        var document = XDocument.Parse(plist);
        var dict = document.Root!.Element("dict")!;
        var keys = dict.Elements("key").Select(e => e.Value).ToList();
        keys.Should().Contain(["Label", "ProgramArguments", "RunAtLoad", "ProcessType", "LimitLoadToSessionType"]);
        keys.Should().NotContain("KeepAlive", "quitting the app must keep it quit");
        dict.Element("array")!.Elements("string").Select(e => e.Value).Should().Equal(
            "/usr/local/share/dotnet/dotnet",
            "/Users/me/Visual Studio Projects/R&D <x>/GitWidget.dll",
            "--autostart");
        plist.Should().Contain("R&amp;D &lt;x&gt;");
    }

    [Fact]
    public void Desktop_entry_quotes_exec_arguments()
    {
        var command = new AutostartCommand("/home/me/Visual Studio Projects/GitWidget", ["--autostart"]);

        string entry = AutostartFormats.XdgDesktopEntry("GitWidget", command);

        entry.Should().StartWith("[Desktop Entry]\n");
        entry.Should().Contain("Type=Application\n");
        entry.Should().Contain("Name=GitWidget\n");
        entry.Should().Contain("Exec=\"/home/me/Visual Studio Projects/GitWidget\" \"--autostart\"\n");
        entry.Should().Contain("X-GNOME-Autostart-enabled=true\n");
    }

    [Theory]
    [InlineData("/opt/a b", "\"/opt/a b\"")]
    [InlineData("/opt/100%", "\"/opt/100%%\"")]
    [InlineData("/opt/$HOME", "\"/opt/\\\\$HOME\"")]
    [InlineData("/opt/q\"x", "\"/opt/q\\\\\"x\"")]
    [InlineData("/opt/back\\slash", "\"/opt/back\\\\\\\\slash\"")]
    public void Desktop_exec_quoting_escapes_reserved_characters(string argument, string expected)
    {
        AutostartFormats.QuoteDesktopExecArgument(argument).Should().Be(expected);
    }

    [Fact]
    public void Control_characters_are_rejected()
    {
        var command = new AutostartCommand("/opt/evil\nExec=rm", ["--autostart"]);

        FluentActions.Invoking(() => AutostartFormats.XdgDesktopEntry("GitWidget", command)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AutostartFormats.LaunchAgentPlist("label", command)).Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => AutostartFormats.WindowsCommandLine(command)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public async Task Mac_launch_agent_enable_and_disable_in_temp_directory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(_root, "Library", "LaunchAgents");
        var service = new MacLaunchAgentAutostartService(directory, "io.github.chwichmann.GitStatusWidget.Test", new AutostartCommand(SpacedExe, ["--autostart"]));

        service.IsSupported.Should().BeTrue();
        (await service.IsEnabledAsync(ct)).Should().BeFalse();

        await service.SetEnabledAsync(true, ct);
        (await service.IsEnabledAsync(ct)).Should().BeTrue();
        service.PlistPath.Should().Be(Path.Combine(directory, "io.github.chwichmann.GitStatusWidget.Test.plist"));
        string content = await File.ReadAllTextAsync(service.PlistPath, ct);
        content.Should().Contain($"<string>{SpacedExe}</string>");
        Directory.GetFiles(directory).Should().ContainSingle("no temporary file is left behind");

        await service.SetEnabledAsync(true, ct); // Idempotent overwrite.
        await service.SetEnabledAsync(false, ct);
        (await service.IsEnabledAsync(ct)).Should().BeFalse();
        await service.SetEnabledAsync(false, ct); // Disabling twice is fine.
    }

    [Fact]
    public async Task Xdg_autostart_enable_and_disable_in_temp_directory()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = XdgAutostartService.DefaultDirectory(Path.Combine(_root, "config"), _root);
        var service = new XdgAutostartService(directory, "gitwidget.desktop", "GitWidget", new AutostartCommand("/home/me/Visual Studio Projects/GitWidget", ["--autostart"]));

        await service.SetEnabledAsync(true, ct);

        service.DesktopFilePath.Should().Be(Path.Combine(_root, "config", "autostart", "gitwidget.desktop"));
        (await service.IsEnabledAsync(ct)).Should().BeTrue();
        (await File.ReadAllTextAsync(service.DesktopFilePath, ct)).Should().Contain("Exec=\"/home/me/Visual Studio Projects/GitWidget\"");

        await service.SetEnabledAsync(false, ct);
        (await service.IsEnabledAsync(ct)).Should().BeFalse();
    }

    [Fact]
    public void Xdg_directory_ignores_relative_config_home()
    {
        XdgAutostartService.DefaultDirectory("relative/dir", "/home/me").Should().Be(Path.Combine("/home/me", ".config", "autostart"));
        XdgAutostartService.DefaultDirectory(null, "/home/me").Should().Be(Path.Combine("/home/me", ".config", "autostart"));
    }

    [Fact]
    public async Task Windows_run_key_writes_quoted_command_via_abstraction()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        var registry = new FakeRunKeyStore();
        var command = new AutostartCommand(@"C:\Users\me\Visual Studio Projects\GitWidget.exe", ["--autostart"]);
        var service = new WindowsRunKeyAutostartService(registry, "GitStatusWidget.Test", command);

        await service.SetEnabledAsync(true, ct);

        registry.Values["GitStatusWidget.Test"].Should().Be("\"C:\\Users\\me\\Visual Studio Projects\\GitWidget.exe\" --autostart");
        (await service.IsEnabledAsync(ct)).Should().BeTrue();

        registry.DisabledByUser.Add("GitStatusWidget.Test");
        (await service.IsEnabledAsync(ct)).Should().BeFalse("Task Manager disabled the entry");

        await service.SetEnabledAsync(true, ct);
        registry.DisabledByUser.Should().BeEmpty("explicit enabling in the app clears the override");

        await service.SetEnabledAsync(false, ct);
        registry.Values.Should().BeEmpty();
        (await service.IsEnabledAsync(ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Enabling_without_command_is_not_supported()
    {
        var service = new WindowsRunKeyAutostartService(new FakeRunKeyStore(), "x", command: null);

        service.IsSupported.Should().BeFalse();
        await FluentActions.Awaiting(() => service.SetEnabledAsync(true, TestContext.Current.CancellationToken)).Should().ThrowAsync<NotSupportedException>();
        await service.SetEnabledAsync(false, TestContext.Current.CancellationToken);
    }

    private sealed class FakeRunKeyStore : IRunKeyStore
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> DisabledByUser { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? GetRunValue(string name) => Values.GetValueOrDefault(name);

        public void SetRunValue(string name, string commandLine) => Values[name] = commandLine;

        public void DeleteRunValue(string name) => Values.Remove(name);

        public bool IsDisabledByUser(string name) => DisabledByUser.Contains(name);

        public void ClearUserOverride(string name) => DisabledByUser.Remove(name);
    }
}

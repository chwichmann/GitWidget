using System.Text;

namespace GitStatusWidget.Infrastructure.Platform;

/// <summary>Builds the per-OS autostart entry text with correct quoting (paths may contain spaces).</summary>
internal static class AutostartFormats
{
    /// <summary>
    /// Windows command line for the Run key: the executable is always quoted, other arguments follow
    /// the <c>CommandLineToArgvW</c> rules.
    /// </summary>
    /// <param name="command">The command.</param>
    /// <returns>The command line.</returns>
    public static string WindowsCommandLine(AutostartCommand command)
    {
        RejectControlCharacters(command);
        if (command.Executable.Contains('"', StringComparison.Ordinal))
        {
            throw new ArgumentException("The executable path contains a quote character.", nameof(command));
        }

        var sb = new StringBuilder().Append('"').Append(command.Executable).Append('"');
        foreach (string argument in command.Arguments)
        {
            sb.Append(' ').Append(QuoteWindowsArgument(argument));
        }

        return sb.ToString();
    }

    /// <summary>Quotes one argument so <c>CommandLineToArgvW</c> returns it unchanged.</summary>
    /// <param name="argument">The argument.</param>
    /// <returns>The argument, quoted when needed.</returns>
    public static string QuoteWindowsArgument(string argument)
    {
        if (argument.Length > 0 && argument.AsSpan().IndexOfAny(" \t\n\v\"") < 0)
        {
            return argument;
        }

        var sb = new StringBuilder().Append('"');
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                sb.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        return sb.Append('\\', backslashes * 2).Append('"').ToString();
    }

    /// <summary>launchd property list for <c>~/Library/LaunchAgents/&lt;label&gt;.plist</c>.</summary>
    /// <param name="label">launchd label (equals the file name without extension).</param>
    /// <param name="command">The command.</param>
    /// <returns>The plist XML.</returns>
    public static string LaunchAgentPlist(string label, AutostartCommand command)
    {
        RejectControlCharacters(command);
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<!DOCTYPE plist PUBLIC \"-//Apple//DTD PLIST 1.0//EN\" \"http://www.apple.com/DTDs/PropertyList-1.0.dtd\">\n");
        sb.Append("<plist version=\"1.0\">\n");
        sb.Append("<dict>\n");
        sb.Append("  <key>Label</key>\n");
        sb.Append("  <string>").Append(EscapeXml(label)).Append("</string>\n");
        sb.Append("  <key>ProgramArguments</key>\n");
        sb.Append("  <array>\n");
        foreach (string argument in command.AllArguments)
        {
            sb.Append("    <string>").Append(EscapeXml(argument)).Append("</string>\n");
        }

        sb.Append("  </array>\n");
        sb.Append("  <key>RunAtLoad</key>\n");
        sb.Append("  <true/>\n");
        sb.Append("  <key>ProcessType</key>\n");
        sb.Append("  <string>Interactive</string>\n");
        sb.Append("  <key>LimitLoadToSessionType</key>\n");
        sb.Append("  <string>Aqua</string>\n");
        sb.Append("</dict>\n");
        sb.Append("</plist>\n");
        return sb.ToString();
    }

    /// <summary>XDG Desktop Entry for <c>$XDG_CONFIG_HOME/autostart</c>.</summary>
    /// <param name="displayName">Value of <c>Name</c>.</param>
    /// <param name="command">The command.</param>
    /// <returns>The file content.</returns>
    public static string XdgDesktopEntry(string displayName, AutostartCommand command)
    {
        RejectControlCharacters(command);
        string exec = string.Join(' ', command.AllArguments.Select(QuoteDesktopExecArgument));
        return "[Desktop Entry]\n"
            + "Type=Application\n"
            + $"Name={EscapeDesktopString(displayName)}\n"
            + "Comment=Repository status widget\n"
            + $"Exec={exec}\n"
            + "Terminal=false\n"
            + "X-GNOME-Autostart-enabled=true\n";
    }

    /// <summary>
    /// Quotes one <c>Exec</c> argument: wrap in double quotes, backslash-escape <c>" ` $ \</c>, double
    /// <c>%</c>. The string-value unescaping of the Desktop Entry format then requires every backslash
    /// to be written twice.
    /// </summary>
    /// <param name="argument">The argument.</param>
    /// <returns>The quoted argument as it appears in the file.</returns>
    public static string QuoteDesktopExecArgument(string argument)
    {
        var sb = new StringBuilder("\"");
        foreach (char c in argument)
        {
            switch (c)
            {
                case '"' or '`' or '$':
                    sb.Append(@"\\").Append(c);
                    break;
                case '\\':
                    sb.Append(@"\\\\");
                    break;
                case '%':
                    sb.Append("%%");
                    break;
                default:
                    sb.Append(c);
                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string EscapeDesktopString(string value) =>
        value.Replace("\\", @"\\", StringComparison.Ordinal);

    private static string EscapeXml(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);

    private static void RejectControlCharacters(AutostartCommand command)
    {
        foreach (string argument in command.AllArguments)
        {
            if (argument.Any(char.IsControl))
            {
                throw new ArgumentException("Autostart arguments must not contain control characters.", nameof(command));
            }
        }
    }
}

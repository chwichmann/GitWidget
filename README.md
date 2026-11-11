# GitWidget

A small, read-only desktop tray widget that shows the state of one or more **GitHub** and **GitLab** repositories: open/closed issues, milestone progress, labels, pull/merge requests and CI pipelines. It polls the provider APIs, so it needs no webhooks and no server.

Built with .NET 10 and [Avalonia](https://avaloniaui.net/). It runs on Windows (primary target), macOS and Linux.

> Status: early development. See [docs/ARCHITECTURE-GitStatusWidget.md](docs/ARCHITECTURE-GitStatusWidget.md).

## Download

Every push to `main` builds self-contained binaries, so no .NET installation is required:

1. Open the [Actions](../../actions) tab and pick the latest successful **CI** run.
2. Download the artifact for your system and unpack it (the artifact download is itself a zip around the archive):
   - **Windows:** `GitWidget-win-x64.zip` → start `GitWidget.exe`.
   - **macOS (Apple Silicon):** `GitWidget-osx-arm64.zip` → move `GitWidget.app` to *Applications*. The app is not notarized: on first start right-click it → *Open* (or allow it under *System Settings → Privacy & Security*). It appears in the menu bar only.
   - **Linux:** `GitWidget-linux-x64.tar.gz` → start `./GitWidget`.

Tagged versions (`v*`) are additionally published as GitHub releases.

**Updating:** when a newer release exists, ⬆ appears in the widget (and an entry in the tray menu). On Windows, **Update to x.y.z and restart** downloads the release, checks it against the SHA-256 checksum GitHub publishes for it, replaces the files and restarts; nothing is installed without that click. The previous version is kept as `*.gitwidget-old` until the new one has started, and is restored if the new one cannot be started. On macOS and Linux, or when GitWidget may not write to its own folder, the dialog offers the release page instead.

## First steps

1. **Start GitWidget.** On Windows the binary is unsigned, so SmartScreen may warn ("More info" → "Run anyway"). It runs without administrator rights. GitWidget lives in the tray (Windows notification area / macOS menu bar). Closing the widget only hides it; use **Exit** in the tray menu to quit.
2. **Add a repository** in Settings (the ⚙ button in the widget, or the tray menu). Pasting the repository URL fills in server and path.
3. **Create a read-only token:**
   - **GitHub:** a fine-grained personal access token limited to the repository, with read-only *Metadata*, *Issues*, *Pull requests* and *Actions*. Add *Contents: Read-only* for the commit counter (commits on the default branch today / in the last 24 hours); without it the counter is simply hidden. For the update notice, the token configured for `chwichmann/GitWidget` additionally needs *Contents: Read-only* (to read releases).
   - **GitLab:** a personal or project access token with only the `read_api` scope (Reporter role or higher; the commit counter needs access to the repository, which a Guest on a private project does not have).
   The token is stored in the OS credential store (Windows Credential Manager, macOS Keychain, Linux Secret Service), never in files.
4. **GitLab behind an access gateway** (for example Citrix) that only admits git clients: set the **User-Agent** of the repository to a git-compatible value such as `GitWidget/0.1.2 git/2.47.0` (the **Use git-compatible** button fills it in). If a gateway intercepts the API anyway, the widget and the dashboard say so ("Blocked by a login gateway") and the dashboard offers **Use suggested User-Agent…**; also check the VPN.
5. Click **Test connection**, then **Save**.

**Claude usage (optional):** *Settings → General → Show the Claude usage at the bottom of the widget* adds a footer with the limits of your Claude subscription (5-hour session, weekly, model-specific weekly) as bars; they turn amber from 75 % and red from 90 %. Click the footer for the full display with the reset times and a pace estimate: at the pace of the last hours (1, 2, 10 or 24, selectable in Settings; default 2), will the limit be reached before it resets, and when? Until enough history is collected the estimate uses the average since the last reset and says so. The tick on each bar marks even use, that is the share of the time that has passed. It uses the existing Claude Code sign-in on this computer (`~/.claude/.credentials.json`, on macOS the Claude Code keychain item): GitWidget only reads it, sends it only to `api.anthropic.com`, and never renews it. If the sign-in has expired, start `claude` once. The usage endpoint is not a documented API and may change.

Logs (no tokens, no issue contents) are written to `%LocalAppData%\GitStatusWidget\logs` on Windows (`~/Library/Application Support/GitStatusWidget/logs` on macOS). Settings and the snapshot cache live next to them.

## Build from source

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```sh
dotnet build GitStatusWidget.slnx
dotnet test GitStatusWidget.slnx
dotnet run --project src/GitStatusWidget.Desktop
```

## Repository layout

| Path | Content |
|---|---|
| `src/GitStatusWidget.Domain` | Provider-neutral domain model (snapshots, events) |
| `src/GitStatusWidget.Application` | Synchronization, snapshot comparison, projections, abstractions |
| `src/GitStatusWidget.Infrastructure` | GitHub/GitLab adapters, persistence, credential stores, OS integration |
| `src/GitStatusWidget.Desktop` | Avalonia UI: tray, widget, dashboard, toasts, settings |
| `tests/*` | xUnit test projects per layer |
| `docs/` | Architecture and API/platform reference notes |
| `tools/` | Helper scripts (for example, icon generation) |

## Security

Tokens are stored only in the operating system's credential store: Windows Credential Manager, the macOS Keychain or the Linux Secret Service. They are never written to settings, logs or caches. The app only reads data from your repositories and never changes anything.

## License

GitWidget is released under the [MIT License](LICENSE).

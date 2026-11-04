# GitWidget

A small, read-only desktop tray widget that shows the state of one or more **GitHub** and **GitLab** repositories: open/closed issues, milestone progress, labels, pull/merge requests and CI pipelines. It polls the provider APIs, so it needs no webhooks and no server.

Built with .NET 10 and [Avalonia](https://avaloniaui.net/). It runs on Windows (primary target), macOS and Linux.

> Status: early development. See [docs/ARCHITECTURE-GitStatusWidget.md](docs/ARCHITECTURE-GitStatusWidget.md).

## Download

Every push to `main` builds self-contained binaries, so no .NET installation is required:

1. Open the [Actions](../../actions) tab and pick the latest successful **CI** run.
2. Download `GitWidget-win-x64`, `GitWidget-osx-arm64` or `GitWidget-linux-x64` from the run's artifacts.
3. Extract the archive and start `GitWidget.exe` (Windows) or `GitWidget` (macOS/Linux).

Tagged versions (`v*`) are additionally published as GitHub releases.

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

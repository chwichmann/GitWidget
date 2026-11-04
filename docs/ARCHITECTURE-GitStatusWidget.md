# Git Repository Status Widget

## Architecture Document

**Status:** Proposed  
**Target:** Windows 11, WPF, .NET 10  
**Supported providers:** GitLab repositories and GitHub repositories  
**Operating mode:** Local polling only, no webhooks, no relay service, no embedded web server  
**Primary audience:** Claude Code and developers implementing the application

---

## 1. Purpose

Git Repository Status Widget is a small Windows desktop application that visualizes the current status of one or more GitLab or GitHub repositories. It runs primarily in the Windows system tray and can display a compact floating widget, an expanded dashboard, and short animated notifications when repository state changes are detected.

The application retrieves repository data by polling the provider APIs. It compares each new provider snapshot with the previously accepted snapshot and derives semantic events such as `IssueCreated`, `IssueClosed`, `IssueReopened`, `MilestoneProgressChanged`, and `PipelineFailed`.

The application is read-only. It must not create, modify, close, label, assign, or otherwise mutate repository content.

---

## 2. Goals

### 2.1 Functional goals

1. Support GitLab and GitHub repositories from the first release.
2. Display:
   - open and closed issue counts;
   - issues grouped by milestone;
   - issues grouped by label;
   - issues without a milestone;
   - issues without an assignee;
   - recently changed issues;
   - recent CI/CD pipeline or workflow status;
   - pull requests or merge requests as a distinct category.
3. Detect changes through polling and snapshot comparison.
4. Animate newly detected events without replaying old events at startup.
5. Operate from the Windows system tray.
6. Provide compact and expanded views.
7. Store credentials securely in Windows Credential Manager.
8. Persist configuration and the last accepted snapshot locally.
9. Handle API pagination, conditional requests, rate limits, transient errors, suspend/resume, and offline operation.
10. Keep the UI and application core provider-neutral.

### 2.2 Quality goals

- Low idle resource usage.
- No server-side component.
- No inbound firewall rule.
- No administrator rights during normal execution.
- Clear separation between provider API contracts and application domain models.
- Deterministic snapshot comparison.
- Testable without calling live GitLab or GitHub instances.
- Safe handling of tokens and sensitive API responses.

---

## 3. Non-goals

The initial product does not include:

- webhooks;
- a SignalR relay;
- a browser-based UI;
- Linux or macOS support;
- repository write operations;
- issue editing;
- GitHub Projects boards;
- GitLab group-level portfolio management;
- cross-device synchronization;
- team chat notifications;
- long-term analytics or data warehousing;
- a Windows service;
- plugin loading from untrusted assemblies.

GitHub support refers to a GitHub repository, not a GitHub Project. GitLab support refers to a GitLab project repository.

---

## 4. Architectural principles

### 4.1 Provider neutrality

The UI, snapshot comparison, persistence, and notification logic must never depend directly on GitLab or GitHub DTOs. Provider-specific clients map external responses into canonical domain snapshots.

### 4.2 Snapshot as source of truth

A successfully completed provider snapshot is the authoritative local view of a repository at a point in time. Animations and notifications are derived by comparing two accepted snapshots.

### 4.3 Read-only integration

Provider tokens receive the minimum read permissions required. The application exposes no command that changes remote state.

### 4.4 Resilient polling

Polling intervals are targets, not guarantees. Authentication failures, rate limits, network failures, standby, and API outages must not corrupt the last valid snapshot.

### 4.5 No false events

A partial or failed refresh must never be compared with a complete previous snapshot. Only complete, validated snapshots are accepted and eligible for event derivation.

### 4.6 Local-first operation

The last valid snapshot remains visible while offline. The UI clearly shows freshness and connection state.

---

## 5. Technology stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 |
| Desktop UI | WPF |
| Presentation pattern | MVVM |
| MVVM library | CommunityToolkit.Mvvm |
| Hosting and DI | Microsoft.Extensions.Hosting |
| HTTP | IHttpClientFactory |
| HTTP resilience | Microsoft.Extensions.Http.Resilience |
| JSON | System.Text.Json |
| Tray integration | H.NotifyIcon.Wpf |
| Logging | Microsoft.Extensions.Logging |
| Local settings | JSON under `%LocalAppData%` |
| Snapshot cache | JSON for MVP |
| Credentials | Windows Credential Manager |
| Tests | xUnit, FluentAssertions, WireMock.Net |

SQLite is deliberately deferred. Introduce `Microsoft.Data.Sqlite` only when historical time-series analysis becomes a requirement.

---

## 6. Solution structure

```text
GitStatusWidget.sln

src/
  GitStatusWidget.Domain/
    Repositories/
    Issues/
    Milestones/
    Labels/
    Builds/
    Events/

  GitStatusWidget.Application/
    Abstractions/
    Synchronization/
    Comparison/
    Projection/
    Notifications/
    Settings/

  GitStatusWidget.Infrastructure/
    Providers/
      GitLab/
      GitHub/
    Persistence/
    Credentials/
    Resilience/
    Time/

  GitStatusWidget.Wpf/
    App.xaml
    Hosting/
    Views/
    ViewModels/
    Controls/
    Animations/
    Tray/
    Themes/
    Assets/

tests/
  GitStatusWidget.Domain.Tests/
  GitStatusWidget.Application.Tests/
  GitStatusWidget.Infrastructure.Tests/
  GitStatusWidget.Wpf.Tests/
```

### Dependency direction

```text
Wpf ------------> Application ------------> Domain
 |                      ^
 |                      |
 +-----> Infrastructure+

Infrastructure ---------------------------> Domain
```

Rules:

- `Domain` references no other solution project.
- `Application` references only `Domain`.
- `Infrastructure` implements interfaces defined by `Application` and uses `Domain`.
- `Wpf` composes the application and may reference `Application`, `Infrastructure`, and `Domain`.
- Provider DTOs remain internal to their provider implementation.

---

## 7. Canonical domain model

The domain model represents concepts shared by GitLab and GitHub. Provider-specific fields may be retained in an extension metadata dictionary only when necessary for diagnostics, never for normal UI logic.

```csharp
public enum RepositoryProviderKind
{
    GitLab = 1,
    GitHub = 2
}

public sealed record RepositoryIdentity(
    RepositoryProviderKind Provider,
    string Host,
    string OwnerPath,
    string RepositoryName,
    string ExternalId);

public enum WorkItemState
{
    Open = 1,
    Closed = 2
}

public sealed record IssueSnapshot(
    string ExternalId,
    int Number,
    string Title,
    WorkItemState State,
    Uri WebUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ClosedAt,
    UserSummary? Author,
    IReadOnlyList<UserSummary> Assignees,
    MilestoneReference? Milestone,
    IReadOnlyList<LabelSnapshot> Labels);

public sealed record MilestoneSnapshot(
    string ExternalId,
    string Title,
    string? Description,
    DateOnly? StartDate,
    DateOnly? DueDate,
    bool IsClosed,
    int OpenIssueCount,
    int ClosedIssueCount);

public sealed record LabelSnapshot(
    string Name,
    string Color,
    string? Description);

public enum ChangeRequestKind
{
    MergeRequest = 1,
    PullRequest = 2
}

public sealed record ChangeRequestSnapshot(
    string ExternalId,
    int Number,
    ChangeRequestKind Kind,
    string Title,
    WorkItemState State,
    bool IsDraft,
    Uri WebUrl,
    DateTimeOffset UpdatedAt);

public enum BuildState
{
    Unknown = 0,
    Queued = 1,
    Running = 2,
    Succeeded = 3,
    Failed = 4,
    Canceled = 5,
    Skipped = 6
}

public sealed record BuildSnapshot(
    string ExternalId,
    string DisplayName,
    BuildState State,
    string? RefName,
    string? CommitSha,
    Uri WebUrl,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);

public sealed record RepositorySnapshot(
    RepositoryIdentity Repository,
    DateTimeOffset RetrievedAt,
    IReadOnlyList<IssueSnapshot> Issues,
    IReadOnlyList<MilestoneSnapshot> Milestones,
    IReadOnlyList<LabelSnapshot> Labels,
    IReadOnlyList<ChangeRequestSnapshot> ChangeRequests,
    IReadOnlyList<BuildSnapshot> Builds,
    SnapshotCompleteness Completeness);
```

`ExternalId` is the provider's stable global identifier where available. The visible issue number or IID is not used as the sole identity.

---

## 8. Provider abstraction

```csharp
public interface IRepositoryStatusProvider
{
    RepositoryProviderKind Kind { get; }

    Task<ProviderConnectionResult> TestConnectionAsync(
        RepositoryConfiguration configuration,
        CancellationToken cancellationToken);

    Task<RepositorySnapshotResult> GetSnapshotAsync(
        RepositoryConfiguration configuration,
        SnapshotRequestContext context,
        CancellationToken cancellationToken);
}
```

`RepositorySnapshotResult` is a discriminated result and must distinguish:

- success with a complete snapshot;
- not modified;
- authentication failure;
- authorization failure;
- repository not found;
- rate limited with retry time;
- transient provider failure;
- malformed or unsupported response;
- partial snapshot, which is not accepted for comparison.

### 8.1 Provider registry

```csharp
public interface IRepositoryStatusProviderResolver
{
    IRepositoryStatusProvider Resolve(RepositoryProviderKind provider);
}
```

Adding a provider must not require changes in dashboard ViewModels or comparison logic.

---

## 9. GitLab adapter

### 9.1 Responsibilities

The GitLab adapter must:

- support GitLab.com and configurable self-managed GitLab base URLs;
- use GitLab REST API v4;
- URL-encode numeric project IDs or project paths correctly;
- follow pagination headers until all required pages are retrieved;
- map GitLab issues separately from merge requests;
- map GitLab pipelines into `BuildSnapshot`;
- map GitLab milestone and label colors into canonical models;
- honor provider rate-limit and retry responses;
- avoid logging tokens and full authorization headers.

### 9.2 Authentication

Use a GitLab Project Access Token when available, otherwise a Personal Access Token. The expected minimum scope is read-only API access appropriate to the GitLab installation.

Send the token through the provider-supported private token header. Credential material is loaded just before request execution and must not be stored in repository configuration JSON.

### 9.3 Repository identity

Store both:

- configured project path, such as `group/subgroup/repository`;
- provider project ID returned by GitLab.

The provider project ID becomes `RepositoryIdentity.ExternalId` after the first successful connection.

---

## 10. GitHub adapter

### 10.1 Responsibilities

The GitHub adapter must:

- support GitHub.com in the MVP;
- allow a configurable API base URL for future GitHub Enterprise Server support;
- use the versioned GitHub REST API;
- include a valid `User-Agent` header;
- use pagination links until all required pages are retrieved;
- distinguish issues from pull requests;
- map GitHub Actions workflow runs into `BuildSnapshot`;
- map milestones and labels into canonical models;
- use ETag-based conditional requests where supported;
- monitor `X-RateLimit-Remaining`, `X-RateLimit-Reset`, and `Retry-After`;
- stop retries when rate limited.

### 10.2 Issue filtering

GitHub's repository issues response may include pull requests. An item containing pull-request metadata must be mapped to `ChangeRequestSnapshot`, not `IssueSnapshot`. Counts displayed as issues must exclude pull requests.

### 10.3 Authentication

For personal use, use a fine-grained Personal Access Token restricted to selected repositories and read-only permissions for metadata, issues, pull requests, and Actions as required.

A GitHub App is outside the MVP but the credential abstraction must not assume that every credential is a long-lived PAT.

### 10.4 Repository identity

The configuration uses `owner/repository`. The stable GitHub repository node or database ID becomes `RepositoryIdentity.ExternalId` after successful connection.

---

## 11. Repository configuration

```csharp
public sealed record RepositoryConfiguration(
    Guid Id,
    RepositoryProviderKind Provider,
    Uri ApiBaseUri,
    string OwnerPath,
    string RepositoryName,
    string CredentialReference,
    TimeSpan IssuePollingInterval,
    TimeSpan BuildPollingInterval,
    TimeSpan MetadataPollingInterval,
    bool IsEnabled);
```

Recommended defaults:

| Data | Interval |
|---|---:|
| Issues and change requests | 30 seconds |
| Pipelines or workflow runs | 30 seconds |
| Milestones and labels | 5 minutes |
| Forced complete reconciliation | 15 minutes |

Intervals use jitter of approximately ±10 percent to avoid synchronized polling if several repositories are configured.

Minimum configurable interval should be 15 seconds. Values below this threshold add little value for a passive widget and increase rate-limit risk.

---

## 12. Synchronization workflow

```text
Periodic trigger or manual refresh
             |
             v
Acquire per-repository refresh lock
             |
             v
Resolve provider and credential
             |
             v
Fetch all required pages and resources
             |
             v
Validate snapshot completeness
             |
       +-----+------+
       |            |
    complete      failed/partial
       |            |
       v            v
Compare with       Keep previous snapshot
previous snapshot  Record health state
       |
       v
Persist new accepted snapshot atomically
       |
       v
Publish derived events
       |
       v
Refresh dashboard projections and animations
```

### 12.1 Concurrency

- Only one refresh per repository may run at a time.
- A manual refresh joins or supersedes a pending scheduled refresh but must not execute concurrently.
- Multiple repositories may refresh concurrently with a conservative global limit, initially two.
- Cancellation during shutdown must leave the prior snapshot intact.

### 12.2 Atomic acceptance

Write snapshots through a temporary file followed by an atomic replace. A snapshot becomes visible to the comparison engine only after complete validation and successful persistence.

### 12.3 First-run behavior

The first valid snapshot establishes the baseline. It updates the dashboard but produces no activity toasts and no open/close animations.

---

## 13. Conditional requests and caching

Maintain provider request metadata independently for each endpoint and repository:

```csharp
public sealed record ConditionalRequestState(
    string ResourceKey,
    string? ETag,
    DateTimeOffset? LastModified,
    DateTimeOffset LastSuccessfulRequest);
```

Rules:

1. Send `If-None-Match` when an ETag exists.
2. Treat `304 Not Modified` as success for that resource.
3. Reuse the corresponding resource section from the last valid snapshot.
4. Never construct a snapshot from missing sections with no previous baseline.
5. Periodically execute a full reconciliation without relying only on incremental assumptions.

---

## 14. Snapshot comparison and domain events

The comparison engine is provider-neutral and deterministic.

```csharp
public interface ISnapshotComparer
{
    IReadOnlyList<RepositoryChangeEvent> Compare(
        RepositorySnapshot previous,
        RepositorySnapshot current);
}
```

### 14.1 Event types

- `IssueCreated`
- `IssueClosed`
- `IssueReopened`
- `IssueTitleChanged`
- `IssueLabelsChanged`
- `IssueMilestoneChanged`
- `IssueAssigneesChanged`
- `MilestoneCreated`
- `MilestoneClosed`
- `MilestoneReopened`
- `MilestoneProgressChanged`
- `ChangeRequestOpened`
- `ChangeRequestClosed`
- `ChangeRequestMerged`
- `BuildStarted`
- `BuildSucceeded`
- `BuildFailed`
- `BuildCanceled`

### 14.2 Event identity and deduplication

Derive a stable local event key from:

```text
RepositoryExternalId + EventType + EntityExternalId + RelevantState + UpdatedAt
```

Persist a bounded recent-event ledger, for example the latest 500 event keys per repository. This prevents duplicate animations after restart or a repeated provider response.

### 14.3 Event priority

| Priority | Examples | UI behavior |
|---|---|---|
| High | Build failed, authentication lost | persistent warning or prominent toast |
| Medium | Issue closed, issue created, change request merged | animated toast |
| Low | label changed, title changed, progress changed | activity feed only by default |

---

## 15. Dashboard projections

ViewModels consume precomputed projections rather than repeatedly grouping raw snapshots.

```csharp
public sealed record DashboardProjection(
    RepositoryHeaderProjection Header,
    IReadOnlyList<MilestoneProgressProjection> Milestones,
    IReadOnlyList<LabelCountProjection> Labels,
    IReadOnlyList<ActivityProjection> RecentActivity,
    IReadOnlyList<BuildProjection> RecentBuilds,
    RepositoryHealthProjection Health);
```

Projection rules must be explicit:

- Issue counts exclude GitHub pull requests and GitLab merge requests.
- Closed issue totals refer to the loaded repository population, not only the current milestone.
- Milestone completion is `closed / (open + closed)` and must handle zero items.
- An issue with several labels contributes to every matching label count.
- Label totals therefore do not sum to the number of issues.
- “Closed today” uses the user's local Windows time zone.
- Provider timestamps are parsed and stored as `DateTimeOffset` in UTC.

---

## 16. WPF user experience

### 16.1 Application modes

1. **Tray mode:** no visible window, application remains active.
2. **Compact widget:** approximately 360 × 190 device-independent pixels.
3. **Expanded dashboard:** approximately 1,000 × 650 device-independent pixels, resizable.
4. **Toast overlay:** short-lived, non-activating event notification.
5. **Settings window:** repository, authentication, polling, startup, and appearance settings.

### 16.2 Compact widget

Display:

- repository name and provider icon;
- freshness indicator;
- open issue count;
- closed issue count;
- selected/current milestone progress;
- last successful update;
- subtle pending refresh indicator.

### 16.3 Expanded dashboard

Display:

- summary cards;
- issues by milestone;
- issues by label;
- recent activities;
- pipeline or workflow status;
- repository/provider selector when multiple repositories exist;
- stale/offline/authentication state.

Use provider branding only for identification. Data colors remain semantically consistent:

- green: succeeded or completed;
- red: failed or blocked;
- blue: active or informational;
- amber: warning or overdue;
- gray: unknown, stale, skipped, or neutral.

### 16.4 Animation rules

- Animations run only for newly derived events.
- No animations during baseline creation.
- Closing an issue shows a green confirmation animation.
- Opening or creating an issue uses a blue entry animation.
- Failed builds use one short red pulse, never a continuous flash.
- Respect the Windows reduced-motion preference.
- Do not block the UI thread while animating or refreshing.

### 16.5 Window behavior

- Persist size, position, selected monitor, and compact/expanded state.
- Clamp restored bounds to currently available working areas.
- Support optional always-on-top.
- Close button hides to tray unless the user explicitly chooses Exit.
- The application should enforce a single running instance and focus/show the existing instance when launched again.

---

## 17. Tray integration

Recommended tray menu:

```text
Git Status Widget
-----------------
Show widget
Open dashboard
Refresh now
-----------------
Repository: owner/repository >
Always on top              [x]
Start with Windows         [x]
Settings
-----------------
Exit
```

The tray icon can indicate aggregate state:

- normal: all configured repositories healthy;
- red badge: at least one latest build failed;
- amber badge: stale data, rate limit, or temporary provider failure;
- gray badge: offline or no repository configured.

Do not encode all state solely by color; expose descriptive tooltip text.

---

## 18. Credential management

### 18.1 Storage

Store tokens in Windows Credential Manager. Configuration stores only a credential reference such as:

```text
GitStatusWidget/{configuration-guid}
```

### 18.2 Rules

- Never persist the token in JSON settings, logs, crash reports, snapshots, or test fixtures.
- Redact `Authorization`, `PRIVATE-TOKEN`, cookies, and query parameters that could contain secrets.
- Load credentials only when required for a request.
- Clear temporary token strings from references as soon as practical, recognizing that .NET strings cannot be reliably zeroed.
- Display only token type and validation status in the settings UI.
- Deleting a repository configuration should optionally remove its credential.

---

## 19. Local storage

```text
%LocalAppData%\GitStatusWidget\
  settings.json
  window-state.json
  cache\
    {repository-config-id}\
      snapshot.json
      request-cache.json
      event-ledger.json
  logs\
    git-status-widget-yyyyMMdd.log
```

### 19.1 Versioning

Every persisted document contains a schema version. Readers must reject unsupported future versions and migrate known older versions explicitly.

### 19.2 Snapshot retention

For the MVP retain only:

- last accepted snapshot;
- recent event ledger;
- current conditional request metadata.

Do not accumulate all snapshots. Historical trend storage is a later feature and should use SQLite rather than an expanding JSON directory.

---

## 20. Error handling and health model

```csharp
public enum RepositoryHealthState
{
    Healthy = 1,
    Refreshing = 2,
    Stale = 3,
    Offline = 4,
    RateLimited = 5,
    AuthenticationFailed = 6,
    AuthorizationFailed = 7,
    NotFound = 8,
    ProviderError = 9,
    ConfigurationError = 10
}
```

### Behavior

| Failure | Behavior |
|---|---|
| Network unavailable | keep last snapshot, exponential backoff, show offline |
| HTTP 401 | stop normal polling, request credential update |
| HTTP 403 | distinguish authorization from rate limiting using headers/body |
| HTTP 404 | show repository not found or inaccessible |
| HTTP 429 | honor `Retry-After`, do not immediately retry |
| HTTP 5xx | retry conservatively, then back off |
| Malformed response | reject refresh, keep last snapshot, log sanitized details |
| Partial pagination failure | reject entire new snapshot |
| Resume from standby | wait a short randomized delay, then refresh |

Suggested retry policy for transient failures: up to three attempts with delays near 1, 3, and 8 seconds plus jitter. Rate-limit responses are not handled by the normal transient retry policy.

A snapshot becomes stale after the greater of three polling intervals or five minutes. Authentication failures remain visible until corrected.

---

## 21. Rate-limit protection

- Prefer authenticated requests.
- Read provider rate-limit headers on every response.
- Use conditional requests.
- Avoid polling labels and milestones every 30 seconds.
- Apply global and per-repository concurrency limits.
- Suspend scheduled calls until the provider reset or retry time.
- The Refresh Now command must respect an active provider-imposed wait.
- Surface remaining/reset information in diagnostics, not the primary dashboard.

---

## 22. Logging and diagnostics

Use structured logging with event IDs. Log:

- application lifecycle;
- selected provider and repository identity;
- refresh start, duration, outcome, and item counts;
- pagination page counts;
- rate-limit state;
- snapshot acceptance or rejection;
- number and type of derived events;
- cache migration and recovery;
- tray and window lifecycle failures.

Never log:

- tokens;
- authorization headers;
- full raw payloads by default;
- private issue descriptions or comments;
- user email addresses unless explicitly required for diagnostics.

Provide a diagnostics view with:

- application version;
- provider;
- API base URI;
- last attempt and last success;
- snapshot age;
- current health state;
- rate-limit reset time;
- cache path;
- sanitized recent errors.

---

## 23. Security boundaries

1. Treat all provider data as untrusted input.
2. Render titles as plain WPF text, never as executable markup or HTML.
3. Validate external URLs before launching them and allow only `https`, with optional `http` only for explicitly configured internal GitLab instances.
4. Do not deserialize polymorphic provider payloads with unrestricted type metadata.
5. Set response size limits appropriate to repository size.
6. Use cancellation and timeouts for every HTTP request.
7. Keep package dependencies minimal and review transitive dependencies.
8. Sign release binaries when the application moves beyond personal development.

---

## 24. Testing strategy

### 24.1 Domain tests

- issue creation, closure, and reopening detection;
- label and milestone changes;
- build state transitions;
- no event on identical snapshots;
- no event storm on baseline;
- deterministic event IDs;
- milestone percentage calculations;
- pull/merge requests excluded from issue counts.

### 24.2 Provider contract tests

Use recorded, sanitized JSON fixtures for both providers:

- normal repository;
- empty repository;
- more than 100 issues requiring pagination;
- issue with no milestone or assignee;
- GitHub issue response containing pull requests;
- GitLab project in a subgroup;
- pipeline/workflow states;
- deleted or renamed labels;
- 304 response;
- 401, 403, 404, 429, and 5xx responses;
- malformed and partial responses.

### 24.3 HTTP integration tests

Use WireMock.Net to validate:

- headers;
- token placement;
- pagination;
- ETag reuse;
- retry behavior;
- rate-limit waiting;
- cancellation;
- response-size and timeout handling.

### 24.4 Persistence tests

- atomic replacement;
- corrupted snapshot recovery;
- schema migration;
- unsupported future version;
- event-ledger deduplication;
- no credential material in persisted files.

### 24.5 WPF tests

Keep UI code thin. Unit-test ViewModels and commands. Add limited UI automation only for critical flows:

- first-run repository setup;
- tray show/hide;
- compact/expanded switch;
- authentication failure display;
- reduced-motion behavior;
- window-bound restoration.

---

## 25. Application startup sequence

1. Enforce single-instance ownership.
2. Build the generic host and dependency injection container.
3. Load and migrate settings.
4. Initialize tray icon.
5. Load cached snapshots and render immediately.
6. Resolve credentials only when refresh begins.
7. Start polling services after UI initialization.
8. Perform initial refresh with jitter.
9. Establish baselines for repositories without cache.
10. Show the widget only if configured; otherwise remain in tray.

---

## 26. Shutdown sequence

1. Stop scheduling new refreshes.
2. Cancel active requests.
3. Await bounded background shutdown.
4. Persist window state and settings.
5. Flush logs.
6. Dispose tray icon explicitly.
7. Release single-instance ownership.

Closing the widget window is not application shutdown. Only the tray Exit command or system shutdown terminates the process.

---

## 27. Evolution path

### Phase 1: vertical slice

- one configured repository;
- GitLab and GitHub provider selection;
- secure credential storage;
- issue snapshot;
- open/closed counts;
- polling and baseline cache;
- compact widget;
- tray lifecycle.

### Phase 2: complete MVP

- milestones and labels;
- pipeline/workflow runs;
- change requests;
- expanded dashboard;
- animations and toast notifications;
- rate-limit handling;
- settings UI;
- autostart;
- complete automated provider tests.

### Phase 3: multi-repository

- repository selector;
- aggregate tray state;
- per-repository intervals;
- bounded parallel refresh;
- repository-specific themes and selected milestones.

### Deferred enhancements

- SQLite history and trends;
- burndown charts;
- GitHub Enterprise Server validation;
- richer self-managed GitLab compatibility matrix;
- Windows notifications;
- signed installer and automatic update mechanism;
- optional relay/webhook mode as a separate architecture decision.

---

## 28. Key architecture decisions

### ADR-001: WPF and Windows only

**Decision:** Use WPF on .NET 10.  
**Reason:** The product is a lightweight Windows tray and desktop widget. WPF provides mature windowing, animation, binding, and system integration without a browser runtime.

### ADR-002: Polling instead of webhooks

**Decision:** Poll provider APIs.  
**Reason:** The application has no inbound endpoint, relay, or server dependency. A 30-second issue/build interval provides acceptable freshness for a passive status widget.

### ADR-003: Canonical snapshot model

**Decision:** Map both providers into one canonical model before comparison and presentation.  
**Reason:** Provider DTO leakage would duplicate UI logic and make provider behavior inconsistent.

### ADR-004: Complete snapshot acceptance

**Decision:** Reject incomplete refreshes.  
**Reason:** Comparing partial data can incorrectly report mass issue closure, label removal, or milestone changes.

### ADR-005: JSON before SQLite

**Decision:** Use versioned JSON for current state and configuration.  
**Reason:** The MVP stores one current snapshot, not analytical history. SQLite becomes justified when durable time-series queries are introduced.

### ADR-006: Windows Credential Manager

**Decision:** Store tokens in Windows Credential Manager.  
**Reason:** Tokens must not be placed in settings files, while a Windows-only product can use the native per-user credential facility.

### ADR-007: Provider-specific HTTP clients

**Decision:** Use separate typed clients for GitLab and GitHub.  
**Reason:** Authentication, headers, pagination, rate limits, response schemas, and endpoint behavior differ materially.

---

## 29. Claude Code implementation rules

Claude Code must follow these constraints while implementing:

1. Do not introduce webhooks, ASP.NET Core hosting, SignalR, Electron, WebView2, MAUI, Avalonia, or a local web server.
2. Do not couple ViewModels to provider DTOs.
3. Do not place access tokens in source code, user secrets intended for development, JSON settings, logs, snapshots, or test output.
4. Do not accept partial snapshots.
5. Do not animate the initial baseline.
6. Do not count GitHub pull requests as issues.
7. Do not count GitLab merge requests as issues.
8. Do not retry rate-limit responses as ordinary transient failures.
9. Do not block the WPF dispatcher with HTTP, file, or credential operations.
10. Do not add SQLite until a historical-data requirement is implemented.
11. Add tests with every provider mapping and comparison behavior.
12. Preserve provider-neutral names in Domain and Application projects.
13. Keep provider-specific terminology inside adapters and presentation labels.
14. Use `TimeProvider` rather than direct `DateTimeOffset.UtcNow` calls in testable application logic.
15. Use cancellation tokens for all I/O and background operations.
16. Treat nullable reference warnings as errors in production projects.
17. Keep public APIs documented and minimize public surface area.
18. Prefer immutable records for snapshots and events.
19. Use atomic file replacement for accepted snapshots.
20. Produce small, reviewable commits aligned with the phases below.

---

## 30. Recommended first implementation milestone

Implement one end-to-end vertical slice before building the full dashboard:

1. Create the solution and dependency structure.
2. Define canonical repository and issue snapshots.
3. Implement settings and Windows Credential Manager abstraction.
4. Implement GitLab issue retrieval and fixture tests.
5. Implement GitHub issue retrieval, PR exclusion, and fixture tests.
6. Implement polling orchestration and complete-snapshot acceptance.
7. Implement JSON snapshot persistence.
8. Implement deterministic issue comparison.
9. Implement a compact WPF widget with open/closed counts.
10. Add tray show, hide, refresh, settings, and exit commands.
11. Verify startup does not emit historical animations.
12. Add issue-opened and issue-closed animations.

The milestone is complete only when the same compact widget works against one GitLab repository and one GitHub repository without provider-specific branches in the ViewModel.

---

## 31. Acceptance criteria for MVP

- A user can configure either a GitLab or GitHub repository.
- The token is stored only in Windows Credential Manager.
- The application starts in the tray and restores the cached dashboard immediately.
- A complete refresh updates issues, milestones, labels, change requests, and recent builds.
- GitHub pull requests are excluded from issue totals.
- GitLab merge requests are excluded from issue totals.
- Opening, closing, and reopening an issue is detected within the configured polling interval.
- No false mass-change event occurs when pagination or one endpoint fails.
- Rate-limit responses pause polling until allowed.
- The compact and expanded views use the same provider-neutral projection.
- Animations respect reduced motion and are not replayed on startup.
- Offline operation retains the last valid snapshot and displays its age.
- Automated tests cover canonical comparison and both provider adapters.
- No secret appears in logs, configuration, snapshots, or test fixtures.

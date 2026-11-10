# GitLab REST API v4 reference for GitWidget

Developer reference for the GitLab adapter (architecture document sections 9, 12, 13, 20, 21).
Target: self-managed **GitLab Enterprise Edition 19.5.1-ee**. GitLab.com must also work.
Scope: read-only polling of issues, milestones, labels, merge requests and pipelines for several projects, including **large projects** (thousands of closed issues and MRs).

## How to read this document

Every statement carries one of these evidence markers:

| Marker | Meaning |
|---|---|
| **[live]** | Observed with an unauthenticated read-only `curl` against GitLab.com public projects on 2026-11-04. GitLab.com runs a pre-release of the next version, so it is a little ahead of 19.5. |
| **[docs]** | Stated on docs.gitlab.com. The URL is listed per section. |
| **[src]** | Read from the GitLab source on the `master` branch (`gitlab.com/gitlab-org/gitlab`), which is slightly newer than 19.5. |
| **[.NET]** | Checked with a small .NET 10.0.400 program on macOS. |
| **Unverified (needs check on 19.5 EE)** | Not confirmed. The commands for checking it are in [section 18](#18-open-questions-to-verify-on-the-1951-ee-instance). |

The instance-specific facts in architecture §9.4 also apply: the target sits behind a gateway, it sends **no `RateLimit-*` headers**, it returns weak ETags and honours `If-None-Match`, and redirects are not followed automatically.

All example JSON is either synthetic or trimmed from public data with personal fields removed.

---

## 0. Key findings and gotchas (summary)

1. **IDs need 64-bit integers.** On GitLab.com, pipeline `id` values are already above 3,000,000,000 (beyond `int.MaxValue`) **[live]**. Use `long` for every `id` and `iid`.
2. **`issues_statistics` ignores `issue_type`.** It counts every work-item type that lives in the issues table: `issue`, `incident`, `task`, `test_case`, `requirement` **[live][src]**. The `issue_type` parameter is not declared for that endpoint, so it is silently dropped.
3. **Without `issue_type`, `/projects/:id/issues` returns more than issues.** It also returns `task`, `incident`, `test_case` and `requirement` items. `requirement` **cannot** be selected with `issue_type`, because the filter accepts only `issue|incident|test_case|task` **[live]**. Filter on the client by `issue_type == "issue"`, or send `issue_type=issue`, and make the counts agree (see 4).
4. **`X-Total` and `X-Total-Pages` are missing above 10,000 results.** The `rel="last"` link is missing too **[live][docs]**. Keyset pagination never sends `X-Total` **[live]**. **There is no `merge_requests_statistics` endpoint**, so REST cannot give exact merged or closed MR counts on large projects. GraphQL can, in one request (see 16).
5. **Milestone objects contain no counts** **[live][src]**. Get per-milestone counts from `issues_statistics?milestone=<title>` (scoped to the project) or from GraphQL `issues(milestoneTitle:…){count}`. Do **not** use GraphQL `milestone.stats`: for an inherited group milestone it counts issues **across the whole group**, not just the project **[live]**.
6. **`labels?with_counts=true` is expensive, and its counts may not be project-scoped for group labels.** The server runs 3 count queries per label. For a `GroupLabel` the counts are computed against the label's group, not the project **[src]**. Unverified (needs check on 19.5 EE).
7. **ETag and `304` work, but they only save bandwidth.** The server builds the full response and then hashes it (Rack::ETag, weak `W/"<md5>"`). A `304` takes the same server time as a `200` (about 1.1 s for 100 issues) and **still counts against the rate limit** (`RateLimit-Observed` went up) **[live]**. Send the ETag back byte-for-byte: the strong form without `W/` does not match and returns `200` **[live]**.
8. **Keyset pagination exists for project issues (since 18.3) but not for MRs or pipelines.** On those endpoints, `pagination=keyset` is **silently ignored** and offset pagination is used **[live]**. On issues, an `order_by` that keyset does not support makes the server drop keyset silently **[src]**.
9. **A moved or renamed project returns `301` to `/projects/<numeric id>`** **[live]**. .NET `HttpClient` **removes `Authorization: Bearer` on redirect but keeps `PRIVATE-TOKEN`** **[.NET]**. Auto-redirect is disabled anyway (architecture §9.4), so handle `301` explicitly and address projects by numeric ID after the first resolution.
10. **Distinguish a proxy that decodes `%2F` from a missing project by the 404 body.** An unencoded or decoded slash gives `{"error":"404 Not Found"}` (no route matched). A missing or inaccessible project gives `{"message":"404 Project Not Found"}` **[live]**.
11. **A self-managed rate limit, once enabled, is low by default.** The authenticated default is 7,200 requests per hour, about 120/min, per user **[docs]**. Ten projects polled every 30 s with 5 requests each come to about 100/min, which is close to that limit. One token shared by all projects is one rate-limit bucket.
12. **The token role matters for counts.** A Guest or Planner token cannot list MRs or pipelines in a private project, and a Guest token does not see confidential issues, so its counts are lower. Use a **Reporter** role with the **`read_api`** scope.
13. **Expired and revoked tokens return `401`** with `{"error":"invalid_token","error_description":"Token is expired. …"}` and a `WWW-Authenticate: Bearer …` header **[src]**. An unknown or garbage token returns `{"message":"401 Unauthorized"}` **[live]**. Handle both shapes.
14. **Issue `web_url` on GitLab.com now points to `/-/work_items/<iid>`**, not `/-/issues/<iid>` **[live]**. Treat `web_url` as opaque and never build or parse it.
15. **The MR `draft` field is current. `work_in_progress` is deprecated but still sent** **[live][docs]**. The `wip` filter is deprecated in favour of `draft` (19.0) **[docs]**.

---

## 1. Authentication

Sources: <https://docs.gitlab.com/api/rest/authentication/>, <https://docs.gitlab.com/user/project/settings/project_access_tokens/>, <https://docs.gitlab.com/user/group/settings/group_access_tokens/>, <https://docs.gitlab.com/user/profile/personal_access_tokens/>, <https://docs.gitlab.com/api/personal_access_tokens/>, <https://docs.gitlab.com/user/permissions/>

### 1.1 Token types

| Type | Created by | Acts as | Availability | Notes |
|---|---|---|---|---|
| **Project access token** (preferred) | Maintainer or Owner of the project | Bot user `project_<id>_bot_<rand>` that is a member of one project with the chosen role | Self-managed: any license. GitLab.com: Premium or Ultimate (a trial allows 1) **[docs]** | Least privilege. The bot is deleted about 30 days after its last token expires or is revoked **[docs]**. |
| **Group access token** | Owner of the group | Bot user that is a member of the group (and so of all its projects) | Same as project tokens | One token covers many projects. Wider blast radius. |
| **Personal access token** | Any user | The user, with all of their permissions | All | Fallback. It sees everything the user sees. |

All three are "personal access tokens" internally. The prefix is the instance's configured PAT prefix, `glpat-` by default. Admins can change it, so **do not validate the token format client-side**.

### 1.2 Scope and role

- **Scope: `read_api`.** It grants read access to the whole REST API and to GraphQL queries (no mutations), which covers every request in this document, including `/metadata` and `/personal_access_tokens/self`. Use `api` only when necessary; it allows writes. `read_repository` does **not** grant REST access to issues.
- **Role for project or group access tokens: Reporter.** For a **private** project:

  | Resource | Guest | Planner (17.7+) | Reporter |
  |---|---|---|---|
  | Issues (non-confidential) | yes | yes | yes |
  | Confidential issues | only ones they authored or are assigned (a bot is neither) | yes | yes |
  | Merge requests | **no** | yes, unless MRs are set to "Only project members" | yes |
  | Pipelines | only if "Project-based pipeline visibility" is enabled | same as Guest | yes |
  | Labels and milestones | yes | yes | yes |
  | Commits (`GET /projects/:id/repository/commits?ref_name=…&since=…`, commit counter, issue #14) | **no** | **no** | yes |

  The pipeline list endpoint checks both `read_pipeline` **and** `read_build` **[src]**, so a project with CI/CD disabled, or a role without job access, fails there.
- A token with too low a role gets `403 Forbidden` or `404` instead of data. With a Guest token, **counts are silently lower**: confidential issues are left out of `issues_statistics` and `X-Total` and are never reported as an error.

### 1.3 Headers

```http
PRIVATE-TOKEN: <token>
```

`PRIVATE-TOKEN` is the documented header. `Authorization: Bearer <token>` also works for PAT, project and group tokens **[docs]**. Recommendation: **use `PRIVATE-TOKEN`**.

- .NET strips `Authorization` when it follows a redirect and keeps `PRIVATE-TOKEN` **[.NET]**. Auto-redirect is disabled, so neither header can leak to another host.
- Never send the token as a `private_token=` query parameter, because it ends up in proxy and access logs.
- Add both header names to the log redaction list (architecture §18.2).

### 1.4 Expiry

- Expiry is mandatory by default (enforced since 16.0). Without an explicit date, the default is 365 days. The maximum lifetime is 365 days by default; on 17.6+ admins can change it (400 days behind the `buffered_token_expiration_limit` flag) **[docs]**. Admins can also turn off the requirement, so `expires_at` can be `null`.
- A token "expires at midnight UTC on the expiry date" **[docs]**. Treat it as **unusable from `expires_at` 00:00 UTC**. Unverified (needs check on 19.5 EE).
- GitLab emails warnings 60, 30 and 7 days ahead. The widget should warn too, starting 14 days ahead.
- How expiry and revocation show up in the API **[src, `lib/api/api_guard.rb`]**:

| Condition | Status | Body | `WWW-Authenticate` |
|---|---|---|---|
| No token or unknown token | 401 | `{"message":"401 Unauthorized"}` **[live]** for a garbage `glpat-…`, or `{"error":"invalid_token","error_description":"Bad Access Token."}` | missing **[live]**, or `Bearer realm="…", error="invalid_token", …` |
| Expired | 401 | `{"error":"invalid_token","error_description":"Token is expired. You can either do re-authorization or token refresh."}` | `Bearer realm="Protected by OAuth 2.0", error="invalid_token", error_description="Token is expired. …"` |
| Revoked | 401 | `{"error":"invalid_token","error_description":"Token was revoked. You have to re-authorize from the user."}` | as above |
| Insufficient scope (for example only `read_user`) | **403** | `{"error":"insufficient_scope","error_description":"The request requires higher privileges than provided by the access token.","scope":"api read_api"}` | `Bearer realm="Protected by OAuth 2.0", error="insufficient_scope", error_description="…", scope="api read_api"` |
| Fine-grained token without the permission | 403 | `{"error":"insufficient_granular_scope", …}` | (newer feature) |

Unverified (needs check on 19.5 EE): the exact body for an expired token on 19.5.

Classification rule:
- `401` → `AuthenticationFailed`. If the body says `expired` or `revoked`, show that reason. Stop polling.
- `403` with `error == "insufficient_scope"` → `AuthorizationFailed` ("token lacks read_api").
- `403` otherwise → `AuthorizationFailed`.

DPoP (17.10+, opt-in per user): if the token owner enabled DPoP, plain PAT calls fail with `401 dpop_error`. Name this cause in the error message, but do not implement DPoP.

### 1.5 Token validation: `GET /personal_access_tokens/self`

```http
GET /api/v4/personal_access_tokens/self
PRIVATE-TOKEN: <token>
```

```json
{
  "id": 4711,
  "name": "gitwidget-readonly",
  "description": "GitWidget tray widget",
  "revoked": false,
  "created_at": "2026-05-01T08:00:00.000Z",
  "scopes": ["read_api"],
  "user_id": 1234,
  "last_used_at": "2026-11-04T07:59:12.345Z",
  "active": true,
  "expires_at": "2027-05-01",
  "last_used_ips": ["10.0.0.5"]
}
```

| Field | Type | Notes |
|---|---|---|
| `expires_at` | `string?` (`YYYY-MM-DD`) | Parse as `DateOnly`. `null` means it never expires. |
| `scopes` | `string[]` | Check that it contains `read_api` or `api`. |
| `active` | `bool` | `false` if expired or revoked. In practice such a token already gets `401` on this call. |
| `user_id` | `long` | For a project or group token, this is the bot user. |

- Any valid token can call it for itself, and `401` means invalid **[docs]**.
- Unverified (needs check on 19.5 EE): that it works for project and group access tokens (expected: yes, they are PATs of bot users).
- Use it in `TestConnectionAsync` and about once a day. Do not use it on every poll.
- `GET /personal_access_tokens/self/associations` lists the groups and projects the token can reach (`min_access_level=20` for Reporter). It could power a project picker in Settings. Optional.

---

## 2. Project addressing

Sources: <https://docs.gitlab.com/api/projects/#get-a-single-project>, <https://docs.gitlab.com/api/rest/#namespaced-paths>, <https://docs.gitlab.com/api/rest/troubleshooting/>

```http
GET /api/v4/projects/:id
```

`:id` is either the numeric project ID or the **URL-encoded full path**: `group%2Fsubgroup%2Frepo`.

### 2.1 Encoding pitfalls in .NET **[.NET]**

| Case | Result |
|---|---|
| `Uri.EscapeDataString("group/sub group/repo")` | `group%2Fsub%20group%2Frepo`. **Correct; use this.** |
| `new Uri("…/projects/group%2Frepo/issues").AbsoluteUri` / `.PathAndQuery` | Keeps `%2F`. .NET Core and .NET 5+ **do not** unescape `%2F`. Only .NET Framework 4.0 did. |
| `uri.ToString()` | Unescapes `%20` to a space but keeps `%2F`. **Never build requests from `ToString()`.** Use `AbsoluteUri` or pass the `Uri` object. |
| `HttpClient.GetAsync("projects/group%2Frepo")` on gitlab.com | Sent as `/api/v4/projects/gitlab-org%2Fgitlab-runner`, returns 200 **[.NET][live]**. |
| `UriBuilder { Path = "…/" + Uri.EscapeDataString(p) }` | Was not double-encoded in this test. Still, never encode twice: `%252F` gives `404 Project Not Found` **[live]**. |
| Base `https://h/gitlab/api/v4` **without a trailing slash** + relative `projects/x` | `https://h/gitlab/api/projects/x`. **The `v4` segment is lost.** |
| Base with a trailing slash + `/projects/x` (leading slash) | `https://h/projects/x`. **The relative root `/gitlab/api/v4` is lost.** |
| Base with a trailing slash + `projects/x` | Correct. |
| Query value `2026-11-04T18:00:00+01:00` left unencoded | `+` reaches the server and is read as a space. **Always send UTC with `Z`**, or encode `+` as `%2B` **[docs]**. |

**Diagnosing a 404:**

- `{"error":"404 Not Found"}` means the route did not match. Typically the slash was not encoded, or a reverse proxy decoded `%2F` **[live]**.
- `{"message":"404 Project Not Found"}` means the project does not exist or the token cannot see it **[live]**.
- Apache in front of GitLab needs `AllowEncodedSlashes NoDecode` and `ProxyPass … nocanon` **[docs]**.

Recommendation: resolve the path once with `GET /projects/<encoded path>`, store the numeric `id`, and use `/projects/<id>/…` afterwards. Numeric IDs avoid the encoding issues, survive renames and transfers, and are what `301` redirects point to.

### 2.2 Moved or renamed projects

A request with an old path returns `301` with `Location: https://host/api/v4/projects/<id>[/subpath?query]` **[live]**. Only `GET` is redirected **[docs]**. With auto-redirect off, the adapter should do the following:

1. If `Location` is on the **same host** and matches `/api/v4/projects/<digits>`, store the new ID, update the stored path on the next successful `GET /projects/:id`, and retry once.
2. Otherwise, classify it as `GatewayInterception` (architecture §9.4).

### 2.3 Relevant fields (authenticated full view)

Unauthenticated calls return a reduced view without counts and access levels **[live]**.

| Field | Type | Notes |
|---|---|---|
| `id` | `long` | → `RepositoryIdentity.ExternalId` |
| `path_with_namespace` | `string` | `group/subgroup/repo` |
| `name`, `name_with_namespace` | `string` | Display name |
| `default_branch` | `string?` | `null` for an empty repository. Use it for `pipelines/latest?ref=`. |
| `web_url` | `string` | |
| `archived` | `bool` | Archived projects are read-only. Poll them less often. |
| `empty_repo` | `bool` | |
| `open_issues_count` | `int` | **Present only when the issues feature is available to the caller** **[src]**. It is a **cached** counter of open, non-hidden items of **all types** (tasks, incidents and so on), public-only unless the caller can read confidential issues **[src]**. It is **not** suitable as "open issues", so prefer `issues_statistics` or GraphQL. |
| `issues_enabled`, `merge_requests_enabled`, `jobs_enabled` | `bool` | Deprecated, "remove in API v5" **[src]**, but present. |
| `issues_access_level`, `merge_requests_access_level`, `builds_access_level`, `repository_access_level` | `string` | `disabled` \| `private` \| `enabled` (plus `public` for some features) |
| `last_activity_at` | `DateTimeOffset` | Changes on pushes and issue or MR activity. Could be used to skip a poll, but it is not reliable for pipelines. Unverified (needs check on 19.5 EE). |

Feature gating: if `issues_access_level == "disabled"`, **skip** issue requests and hide the section (architecture §16.2), rather than calling and handling `403` or `404`. The same applies to `merge_requests_access_level` and to `builds_access_level` for pipelines. Unverified (needs check on 19.5 EE): whether a disabled feature returns `403` or `404`.

```json
{
  "id": 4242,
  "name": "widget",
  "path_with_namespace": "acme/tools/widget",
  "default_branch": "main",
  "web_url": "https://gitlab.example.com/gitlab/acme/tools/widget",
  "archived": false,
  "empty_repo": false,
  "open_issues_count": 312,
  "issues_access_level": "enabled",
  "merge_requests_access_level": "enabled",
  "builds_access_level": "private",
  "last_activity_at": "2026-11-04T19:22:47.458Z"
}
```

GitLab.com limits `GET /projects/:id` to **400/min per user** **[docs]**. Call it at connection time and during reconciliation only, not on every poll.

---

## 3. Issues

Sources: <https://docs.gitlab.com/api/issues/#list-project-issues>, `lib/api/issues.rb` **[src]**

```http
GET /api/v4/projects/:id/issues
```

### 3.1 Query parameters used by GitWidget

| Param | Values | Default | Notes |
|---|---|---|---|
| `state` | `opened` \| `closed` \| `all` | **`all`** **[live][src]** | Always send it explicitly. |
| `updated_after` / `updated_before` | ISO 8601 | — | **Inclusive** ("on or after") **[src]**. Send UTC `…Z`. Invalid input → `400 {"error":"updated_after is invalid"}` **[live]**. |
| `order_by` | `created_at`, `updated_at`, `priority`, `due_date`, `relative_position`, `label_priority`, `milestone_due`, `popularity`, `weight`, `title` | `created_at` | `id` is **rejected** (`400 order_by does not have a valid value`) **[live]**, even though the docs list it for keyset. |
| `sort` | `asc` \| `desc` | `desc` | |
| `per_page` | 1–100 | 20 | Values above 100 are **silently clamped to 100** **[live]**. |
| `issue_type` | `issue` \| `incident` \| `test_case` \| `task` | none (all types) | Any other value → `400 issue_type does not have a valid value` (for example `objective`, `key_result`, `epic`, `requirement`) **[live]**. |
| `milestone` | title \| `None` \| `Any` | — | Exact title. The `None` and `Any` wildcards via `milestone` are "planned for removal" **[src]**, so use `milestone_id` for wildcards. |
| `milestone_id` | `None` \| `Any` \| `Upcoming` \| `Started` only | — | **Not a numeric ID**: `milestone_id=7324682` → `400` **[live]**. Mutually exclusive with `milestone`. |
| `labels` | comma-separated names (AND), `None`, `Any` | — | |
| `assignee_id` | user ID \| `None` \| `Any` | — | `assignee_id=None` works **[live]**. |
| `with_labels_details` | `true`/`false` | `false` | `true` turns `labels` into objects with colours (see 3.3). |
| `confidential` | `true`/`false` | — | |
| `iids[]` | ints | — | Fetch a specific set, for example to re-check items that disappeared. |
| `pagination=keyset` + `cursor` | — | — | 18.3+ **[docs]**. See section 9. |

### 3.2 Response fields

| Field | Type | Null? | Map to |
|---|---|---|---|
| `id` | `long` | no | `IssueSnapshot.ExternalId` (global, stable) |
| `iid` | `long` | no | `Number` (project-scoped, shown as `#iid`) |
| `project_id` | `long` | no | |
| `title` | `string` | no | |
| `state` | `"opened"` \| `"closed"` | no | `WorkItemState` |
| `created_at`, `updated_at` | ISO 8601 (ms, `Z`) | no | `DateTimeOffset` |
| `closed_at` | ISO 8601 | **yes** | `ClosedAt` |
| `closed_by` | user | yes | |
| `labels` | `string[]`, or objects with `with_labels_details=true` | no (may be `[]`) | |
| `milestone` | milestone object (section 6), no counts | yes | `MilestoneReference` (`id` + `title`). A group milestone has `group_id` instead of `project_id` **[live]**. |
| `assignees` | `user[]` | no (may be `[]`) | |
| `assignee` | user | yes | **Deprecated.** Use `assignees`. |
| `author` | user | no | |
| `issue_type` | `"issue"` \| `"incident"` \| `"test_case"` \| `"task"` \| `"requirement"` … | no | Filter on this. |
| `type` | `"ISSUE"` \| `"INCIDENT"` \| `"TASK"` \| `"REQUIREMENT"` … | no | Upper-case form of the same value **[live]** |
| `confidential` | `bool` | no | |
| `web_url` | `string` | no | Opaque. Points to `/-/work_items/<iid>` on GitLab.com **[live]**. |
| `moved_to_id` | `long` | yes | Set when an issue was moved to another project; it is then `closed`. |
| `references.full` | `string` | no | `group/proj#123` |
| `epic`, `epic_iid`, `iteration`, `weight`, `health_status` | | yes | EE fields. `epic_iid` is deprecated (v5). |
| `task_completion_status` | `{count, completed_count}` | no | |

User object: `{id, username, name, state, locked, avatar_url, web_url, public_email}` (`public_email` is often `""`). Map it to `UserSummary` and never log it.

Synthetic example (trimmed):

```json
[
  {
    "id": 900001,
    "iid": 42,
    "project_id": 4242,
    "title": "Tray icon flickers on resume",
    "state": "closed",
    "created_at": "2026-10-01T09:12:00.000Z",
    "updated_at": "2026-11-04T08:30:11.120Z",
    "closed_at": "2026-11-04T08:30:11.090Z",
    "labels": ["bug", "priority::high"],
    "milestone": { "id": 501, "iid": 7, "project_id": 4242, "title": "v1.2", "state": "active",
                   "due_date": "2026-11-30", "start_date": "2026-11-01", "expired": false,
                   "web_url": "https://gitlab.example.com/acme/tools/widget/-/milestones/7" },
    "assignees": [{ "id": 77, "username": "jdoe", "name": "J. Doe", "state": "active",
                    "avatar_url": null, "web_url": "https://gitlab.example.com/jdoe" }],
    "author":    { "id": 78, "username": "asmith", "name": "A. Smith", "state": "active",
                    "avatar_url": null, "web_url": "https://gitlab.example.com/asmith" },
    "type": "ISSUE",
    "issue_type": "issue",
    "confidential": false,
    "moved_to_id": null,
    "web_url": "https://gitlab.example.com/acme/tools/widget/-/issues/42"
  }
]
```

### 3.3 Label colours on issues

With `with_labels_details=true`, each label becomes an object **[live]**:

```json
{ "id": 11, "name": "priority::high", "color": "#d9534f", "text_color": "#FFFFFF",
  "description": "Fix first", "description_html": "Fix first", "archived": false }
```

This roughly doubles the payload, and the `/labels` endpoint already returns colours. **Recommendation:** keep `with_labels_details=false` and join colours from the label list (section 7) by name. Labels not in the project's or ancestors' label list cannot occur.

### 3.4 Work items and issue types (GitLab 17–19)

- Since about 17.x, issues and tasks are "work items" in one table. **[live]** on GitLab.com: the default `/projects/:id/issues?state=opened` list for `gitlab-org/gitlab-runner` returned 2,582 items: 2,570 `issue`, 8 `task`, 3 `incident`, 1 `requirement`.
- **Epics** are group-level and do **not** appear in project issue lists. The Epics REST API is deprecated (17.0, removal in v5) **[docs]**. Objectives and key results (OKR, Ultimate) are not accepted by `issue_type`. Unverified (needs check on 19.5 EE): whether they appear in the unfiltered list (expected: no, as with epics).
- There is **no** REST `/projects/:id/work_items` endpoint (`404`) **[live]**. Work items are GraphQL-only.
- **Recommendation:** send `issue_type=issue` on list requests. Then make **all counts issue-only too**: `issues_statistics` cannot do that (see 4), so use GraphQL `types:[ISSUE]` or `X-Total` with `issue_type=issue`. Show incidents optionally, as a later setting.
- Converting an issue to a task changes `issue_type` and bumps `updated_at`. The delta query with `issue_type=issue` then **stops returning it**, and the item seems to vanish. Treat "was open in the snapshot and is now missing from the open set" as **"left the population"**, not as `IssueClosed` (architecture §4.5). Resolve the ambiguity with `iids[]=<n>` without `issue_type`.

### 3.5 Confidential issues

They are returned only if the caller has Planner or Reporter+ (or authored or is assigned to them). Otherwise they are silently absent from lists, `X-Total`, `issues_statistics`, label counts and `open_issues_count`. With a Reporter token everything is consistent. Changing a token's role changes counts without any event, so after a credential change, re-baseline like a first run (architecture §12.3).

---

## 4. Issue counts

Sources: <https://docs.gitlab.com/api/issues_statistics/>, `lib/api/issues.rb` **[src]**

```http
GET /api/v4/projects/:id/issues_statistics
```

```json
{ "statistics": { "counts": { "all": 9518, "closed": 6936, "opened": 2582 } } }
```

That response is **[live]** for `gitlab-org/gitlab-runner`.

| Aspect | Finding |
|---|---|
| Shape | Always `{statistics:{counts:{all,closed,opened}}}`, all `int`. |
| `state` param | **Ignored**: all three counts always come back **[live]**. |
| Supported filters | `milestone` (title, `None`, `Any`), `milestone_id` (`None`, `Any`, `Upcoming`, `Started`), `labels`, `iids[]`, `author_*`, `assignee_*`, `search`/`in`, `created_*`, `updated_*`, `confidential`, `scope`, `my_reaction_emoji`, `not[…]` **[src]**. `milestone=19.9` gave `opened:2`, matching the list's `X-Total` **[live]**. |
| `issue_type` | **Silently ignored** **[live]**: `?issue_type=task` returned the unfiltered totals. Counts include tasks, incidents, test cases and requirements. |
| Milestone scoping | `milestone=<group milestone title>` counts **only this project's** issues: 12 for "18.3", versus 2,594 group-wide **[live]**. |
| Unknown milestone title | `{all:0,closed:0,opened:0}`, not an error **[live]**. |
| Cost | Three `COUNT`s on the server: 0.1–0.3 s on a 9.5k-issue project, **about 4 s on `gitlab-org/gitlab` (326k issues)** **[live]**. ETag and `304` are supported (a 304 still costs that time). |
| Visibility | Counts only what the caller can see (confidential issues need Reporter or Planner). |

### 4.1 `X-Total` as a count source

`GET …/issues?state=closed&issue_type=issue&per_page=1` (or `HEAD`, which also returns `X-Total` with an empty body **[live]**) gives an exact issue-only count, **but only up to 10,000**. Above that, `X-Total`, `X-Total-Pages` and `rel="last"` are **omitted** **[docs][live]**. On `gitlab-org/gitlab` the headers were absent for both `state=all` and `state=opened`.

The adapter must treat a missing `X-Total` as "≥ 10,000 / unknown" and never as 0. Keyset responses never contain `X-Total` **[live]**.

### 4.2 Count source decision

| Need | Best source |
|---|---|
| Open and closed issues, issue type only, any size | **GraphQL** `issues(state:…, types:[ISSUE]){count}`: exact, 1 request for all counts (section 16) |
| Open and closed issues, all types | `issues_statistics` (1 request) |
| Per-milestone open and closed | `issues_statistics?milestone=<title>` (1 request per milestone), or GraphQL aliases (1 request in total) |
| Merged and closed MRs | GraphQL `mergeRequests(state:merged){count}`. REST only via `X-Total`, which fails above 10k. |

---

## 5. Merge requests

Source: <https://docs.gitlab.com/api/merge_requests/#list-project-merge-requests>

```http
GET /api/v4/projects/:id/merge_requests?state=opened&per_page=100&order_by=updated_at&sort=desc
```

| Param | Values | Notes |
|---|---|---|
| `state` | `opened`, `closed`, `locked`, `merged`, `all` | Default `all`. `locked` is a short-lived transitional state during merge, and the list is usually empty (`X-Total: 0`) **[live]**. |
| `updated_after` / `updated_before` | ISO 8601 | For the delta query |
| `order_by` | `created_at` (default), `updated_at`, `merged_at` (17.2+), `title`, `priority`, `label_priority`, `milestone_due`, `popularity` | `id` → `400` **[live]** |
| `draft` | `true`/`false` | 19.0+. Replaces `wip` (deprecated) **[docs]** |
| `view=simple` | | Returns only `id, iid, project_id, title, description, state, created_at, updated_at, web_url`. **No `draft` and no `merged_at`**, so it is not suitable. |
| `with_merge_status_recheck` | | Do **not** set it: it triggers expensive mergeability rechecks. |
| `pagination=keyset` | | **Ignored**: falls back to offset **[live]**. |

| Field | Type | Null? | Notes |
|---|---|---|---|
| `id` | `long` | no | `ExternalId` |
| `iid` | `long` | no | `Number` (`!iid`) |
| `state` | `opened` \| `closed` \| `merged` \| `locked` | no | `merged` and `closed` → `WorkItemState.Closed`, with the merged/closed distinction kept for `ChangeRequestMerged` and `ChangeRequestClosed`. `locked` → treat as `opened` (transitional). |
| `draft` | `bool` | no | **Use this** for `IsDraft`. |
| `work_in_progress` | `bool` | no | Deprecated alias of `draft`. Still present **[live]**. Do not read it. |
| `merged_at` | ISO 8601 | yes | |
| `closed_at` | ISO 8601 | yes | |
| `merge_user` | user | yes | `merged_by` is deprecated (v5) |
| `detailed_merge_status` | string | no | `merge_status` is deprecated (v5). Not needed by the widget. |
| `updated_at` | ISO 8601 | no | |
| `web_url`, `title`, `source_branch`, `target_branch`, `labels`, `milestone`, `author`, `assignees`, `reviewers` | | | `author.bot` (`bool`) was added in 19.5 **[docs][live]** |

```json
{
  "id": 562009079, "iid": 7778, "project_id": 4242,
  "title": "Draft: Show helper image version",
  "state": "opened", "draft": true, "work_in_progress": true,
  "created_at": "2026-11-04T19:15:09.047Z", "updated_at": "2026-11-04T19:18:29.377Z",
  "merged_at": null, "closed_at": null, "merge_user": null,
  "source_branch": "feature/x", "target_branch": "main",
  "detailed_merge_status": "draft_status",
  "web_url": "https://gitlab.example.com/acme/tools/widget/-/merge_requests/7778"
}
```

There is no MR statistics endpoint. For counts, see 4.2 and 16.

---

## 6. Milestones

Sources: <https://docs.gitlab.com/api/milestones/>, <https://docs.gitlab.com/api/group_milestones/>, `lib/api/entities/milestone.rb` **[src]**

```http
GET /api/v4/projects/:id/milestones?include_ancestors=true&per_page=100
```

| Param | Notes |
|---|---|
| `state` | `active` \| `closed`. Omit it to get both. |
| `include_ancestors` | `true` adds milestones of all parent groups (inherited). `include_parent_milestones` is deprecated. |
| `title`, `search`, `search_title`, `iids[]` | `iids[]` is ignored when `include_ancestors=true` |
| `updated_after` / `updated_before` | Usable for the delta query |
| `sort` | Default `due_date_asc` |

Milestone entity, complete **[src][live]**:

| Field | Type | Null? | Notes |
|---|---|---|---|
| `id` | `long` | no | Global, `ExternalId` |
| `iid` | `long` | no | Scoped to its project **or group** |
| `project_id` | `long` | *absent* for group milestones | |
| `group_id` | `long` | *absent* for project milestones | |
| `title` | `string` | no | Unique within project + ancestors in practice, but **not guaranteed**: a project and an ancestor group can both have "v1.0". Unverified (needs check on 19.5 EE): does `milestone=v1.0` then match both? |
| `description` | `string?` | yes | |
| `state` | `active` \| `closed` | no | |
| `created_at`, `updated_at` | ISO 8601 | no | |
| `start_date`, `due_date` | `YYYY-MM-DD` | yes | → `DateOnly?` |
| `expired` | `bool` | no (may be `null` on old data) | `due_date` is in the past |
| `web_url` | `string` | no | Group milestone: `/groups/<g>/-/milestones/<iid>` |

**The milestone object has no issue counts and no progress** **[src][live]**. Options for `MilestoneSnapshot.OpenIssueCount` and `ClosedIssueCount`:

1. `GET /projects/:id/issues_statistics?milestone=<url-encoded title>`: one request per milestone. Project-scoped and correct for group milestones **[live]**. Includes all issue types.
2. GraphQL with aliases `m1o: issues(state: opened, milestoneTitle:["v1.2"], types:[ISSUE]){count}`: all milestones in **one** request, issue-only, project-scoped **[live]**.
3. `GET /projects/:id/milestones/:milestone_id/issues` (`:milestone_id` = **global `id`**) returns full issues, which is far too heavy for counting. Unverified (needs check on 19.5 EE): whether it works for an inherited group milestone through the project route (expected: `404`; the group route `/groups/:gid/milestones/:id/issues` spans all group projects).
4. **Do not** use GraphQL `Milestone.stats {totalIssuesCount closedIssuesCount}`. For a group milestone it is group-wide: 2,594 for "18.3", versus 12 in the project **[live]**.

**Recommendation:** count only active milestones, plus the milestone selected in the compact widget. Closed milestones change rarely, so count them once during reconciliation.

```json
[
  { "id": 501, "iid": 7, "project_id": 4242, "title": "v1.2", "description": null,
    "state": "active", "created_at": "2026-10-01T08:00:00.000Z", "updated_at": "2026-10-01T08:00:00.000Z",
    "start_date": "2026-11-01", "due_date": "2026-11-30", "expired": false,
    "web_url": "https://gitlab.example.com/acme/tools/widget/-/milestones/7" },
  { "id": 7324682, "iid": 147, "group_id": 9970, "title": "19.9", "description": "",
    "state": "active", "created_at": "2026-03-03T23:00:43.030Z", "updated_at": "2026-03-03T23:00:43.030Z",
    "start_date": "2027-01-09", "due_date": "2027-02-12", "expired": false,
    "web_url": "https://gitlab.example.com/groups/acme/-/milestones/147" }
]
```

Note: on GitLab.com, `/projects/:id/milestones` and `/labels` return `401` **without authentication, even for public projects** **[live]**. Their shapes above come from the source and from the embedded milestone object in issues.

---

## 7. Labels

Source: <https://docs.gitlab.com/api/labels/>, `lib/api/entities/label*.rb`, `app/models/label.rb` **[src]**

```http
GET /api/v4/projects/:id/labels?include_ancestor_groups=true&per_page=100
```

| Param | Default | Notes |
|---|---|---|
| `include_ancestor_groups` | **`true`** | Group labels are included by default. |
| `with_counts` | `false` | See below. |
| `search` | | |
| `archived` | unset = all | `archived` attribute added in 18.3 behind a flag, GA in 18.10 **[docs]**. Hide archived labels in the UI. |

| Field | Type | Null? | Notes |
|---|---|---|---|
| `id` | `long` | no | |
| `name` | `string` | no | Scoped labels use `scope::value` |
| `color` | `string` `#RRGGBB` | no | Can be lower or upper case. Normalise it. |
| `text_color` | `string` `#RRGGBB` | no | Computed contrast colour (`#FFFFFF` or `#1F1E24`) **[live]** |
| `description` | `string` | yes | |
| `description_html` | `string` | no | Do not render it. |
| `subscribed` | `bool` | no | |
| `priority` | `int` | yes | Project label priority |
| `is_project_label` | `bool` | no | `false` → inherited group label |
| `archived` | `bool` | no | 18.3+ |
| `open_issues_count`, `closed_issues_count`, `open_merge_requests_count` | `int` | **only present when `with_counts=true`** **[src]** | |

What `with_counts=true` does **[src]**:

- It adds exactly these three fields: `open_issues_count`, `closed_issues_count` and `open_merge_requests_count`. There is no closed or merged MR count.
- Each field is a separate `IssuesFinder` or `MergeRequestsFinder` `COUNT` **per label**, so 3 queries per label. With 200 labels that is 600 count queries per page.
- The counts respect the caller's visibility and include **all issue types**.
- For a **group label**, the finder is scoped to the label's **group** (`group_id`), so the counts are **group-wide**, not project-only **[src]**. Unverified (needs check on 19.5 EE).

**Recommendation:** fetch labels **without** `with_counts`, every 5 minutes, for names, colours and descriptions. Compute per-label counts locally:

- Open counts come from the fully fetched open-issue set, which is exact and project-scoped.
- If closed counts per label are needed, use `issues_statistics?labels=<name>` for the handful of labels the user pins.

---

## 8. Pipelines

Source: <https://docs.gitlab.com/api/pipelines/>, `lib/api/ci/pipelines.rb`, `app/models/concerns/ci/has_status.rb` **[src]**

### 8.1 Endpoints

| Endpoint | Use |
|---|---|
| `GET /projects/:id/pipelines?per_page=20&order_by=id&sort=desc` | Recent pipelines (list fields only) |
| `GET /projects/:id/pipelines?updated_after=<t>&order_by=updated_at&sort=desc` | Delta: pipelines whose status changed |
| `GET /projects/:id/pipelines?ref=<default_branch>&per_page=1` | Latest pipeline on a branch, any commit |
| `GET /projects/:id/pipelines/latest?ref=<branch>` | Latest pipeline **for the branch's head commit**, with full fields. **Returns `403` if the head commit has no pipeline** **[docs][src]**, so treat that `403` as "no build". |
| `GET /projects/:id/pipelines/:pipeline_id` | Full fields (`started_at`, `finished_at`, `duration`, `detailed_status`, `user`, `coverage`) |

List parameters:

| Param | Values |
|---|---|
| `scope` | `running`, `pending`, `finished`, `branches`, `tags` (`branches`/`tags` = latest pipeline per ref) |
| `status` | one of the 13 statuses below (others → `400`) |
| `source` | `push`, `web`, `trigger`, `schedule`, `api`, `external`, `pipeline`, `chat`, `webide`, `merge_request_event`, `external_pull_request_event`, `parent_pipeline`, `ondemand_dast_scan`, `ondemand_dast_validation`, `security_orchestration_policy`, `container_registry_push`, `duo_workflow`, `pipeline_execution_policy_schedule`, `dependency_management_security_update` **[src]**. Treat it as an **open set**: unknown values must not break parsing. |
| `ref`, `sha`, `name`, `username`, `yaml_errors` | |
| `created_after/before`, `updated_after/before` | ISO 8601 |
| `order_by` | `id` (default), `status`, `ref`, `updated_at`, `user_id` |
| `pagination=keyset` | **Ignored**: falls back to offset **[live]** |

Child pipelines (`source=parent_pipeline`) are excluded from the list unless `source=parent_pipeline` is requested (17.0+). That is the right default for the widget.

### 8.2 Fields

| Field | List | Single and `latest` | Type | Null? |
|---|---|---|---|---|
| `id` | ✔ | ✔ | **`long` (> 2³¹ on GitLab.com)** | no |
| `iid` | ✔ | ✔ | `long` | no |
| `project_id` | ✔ | ✔ | `long` | no |
| `sha` | ✔ | ✔ | `string` (40 hex) | no |
| `ref` | ✔ | ✔ | `string` (branch, tag, or `refs/merge-requests/<iid>/head` for MR pipelines) | no |
| `status` | ✔ | ✔ | enum string | no |
| `source` | ✔ | ✔ | enum string (open set) | no |
| `name` | ✔ | ✔ | `string?` (from `workflow:name`; usually `null` **[live]**) | yes |
| `created_at`, `updated_at` | ✔ | ✔ | ISO 8601 | no |
| `web_url` | ✔ | ✔ | `string` | no |
| `started_at`, `finished_at` | ✘ | ✔ | ISO 8601 | yes |
| `duration`, `queued_duration` | ✘ | ✔ | `int?` seconds | yes |
| `detailed_status` | ✘ | ✔ | object (`text`, `label`, `group`, `icon`) | no |
| `before_sha`, `tag`, `yaml_errors`, `committed_at`, `coverage`, `user`, `archived` | ✘ | ✔ | | |

That the list omits `started_at` and `finished_at` is confirmed **[live][docs]**. For `BuildSnapshot.CompletedAt`, use one of these:

- Use `updated_at` from the list as an approximation once the status is terminal.
- Fetch `/pipelines/:id` **once** when a pipeline turns terminal. That is one extra request per finished pipeline, which is cheap.

`DisplayName`: `name ?? ref`, plus `#iid`.

```json
[
  { "id": 3028771539, "iid": 54761, "project_id": 4242,
    "sha": "dc9a8ed4fe020c69915a3b0a43c8b262d07f865b", "ref": "main",
    "status": "running", "source": "push", "name": null,
    "created_at": "2026-11-04T19:40:07.092Z", "updated_at": "2026-11-04T19:41:41.088Z",
    "web_url": "https://gitlab.example.com/acme/tools/widget/-/pipelines/3028771539" }
]
```

### 8.3 Status values and mapping to `BuildState`

All 13 values are accepted by `status=` **[live][src]**:

| GitLab `status` | Meaning | `BuildState` | Terminal? |
|---|---|---|---|
| `created` | Pipeline created, jobs not yet processed | `Queued` | no |
| `waiting_for_resource` | Waiting for a `resource_group` lock | `Queued` | no |
| `preparing` | Runner preparing the environment | `Queued` | no |
| `waiting_for_callback` | A job waits for an external callback (17.x+) | `Running` | no |
| `pending` | Waiting for a runner | `Queued` | no |
| `running` | Executing | `Running` | no |
| `success` | All required jobs passed (manual jobs with `allow_failure` do not block) | `Succeeded` | yes |
| `failed` | A required job failed | `Failed` | yes |
| `canceling` | Cancel requested, jobs shutting down (16.x+) | `Canceled`* | almost |
| `canceled` | Canceled | `Canceled` | yes |
| `skipped` | All jobs skipped (rules or `when`) | `Skipped` | yes |
| `manual` | **Blocked** on a manual job with `allow_failure: false` | `Queued`** | no (can last forever) |
| `scheduled` | Waiting for a delayed (`when: delayed`) job | `Queued` | no |
| any other or unknown value | | `Unknown` | |

\* Mapping `canceling` straight to `Canceled` fires `BuildCanceled` once, at the earliest moment. The following `canceled` is the same state and fires no second event. The alternative is `Running`.

\*\* The enum has no "blocked" or "waiting for user" state. Suggestion: add `BuildState.Blocked` later. Until then, map `manual` to `Queued` and keep `CompletedAt = null`.

`BuildStarted` should fire on a transition into `Running` from a non-running state, not on `created` → `pending`.

---

## 9. Pagination

Sources: <https://docs.gitlab.com/api/rest/#pagination>, <https://docs.gitlab.com/api/rest/#keyset-based-pagination>

### 9.1 Offset (default)

- `page` (1-based) and `per_page` (default 20, max 100; larger values are silently clamped **[live]**).
- Response headers **[live]**:

```http
x-page: 1
x-per-page: 100
x-next-page: 2            (empty string on the last page)
x-prev-page:              (empty string on the first page)
x-total: 2582             (omitted if > 10,000)
x-total-pages: 26         (omitted if > 10,000)
link: <https://…&page=2&per_page=100…>; rel="next", <…page=1…>; rel="first", <…page=26…>; rel="last"
```

- Follow `rel="next"` or `x-next-page` until it is empty. **Do not stop based on `x-total-pages`**, because it can be absent. A page past the end returns `200 []` **[live]**.
- The `Link` URLs repeat every effective parameter, including defaults such as `state=all` and `with_labels_details=false` **[live]**. They are handy for debugging which defaults applied. Validate that the link host and path prefix match the configured base URL before following it (gateway defence, architecture §9.4).
- **Offset pagination is unstable under concurrent changes.** An issue updated between page 1 and page 2 can shift, so it appears twice or is skipped. Order by `created_at`/`id`-like stable keys when fetching full sets, de-duplicate by `id`, and for the open set compare the total against `X-Total` (or the count source). If they differ, retry once before accepting.

### 9.2 Keyset

- Enable with `pagination=keyset` + `order_by` + `sort`. The next page is given **only** in `Link: <…cursor=…>; rel="next"` (issues) or `id_after=` (projects). There is **no `x-next-page` and no `x-total`** **[live]**. The end is reached when `Link` is absent or the array is empty.
- Support:
  - **Project issues: yes, since 18.3** **[docs]**. Verified with `order_by` = `created_at`, `updated_at` and `title` **[live]**. `id` is rejected by `order_by` validation **[live]**, and an `order_by` that keyset does not support makes the server **silently drop** keyset **[src]**.
  - Projects list: yes (`order_by=id`).
  - **Merge requests: no**, silently offset **[live]**.
  - **Pipelines: no**, silently offset **[live]**. Pipeline jobs only (`/jobs`).
- Recommended use: a **full scan of all issues**, if ever needed, should use keyset `order_by=updated_at&sort=asc`. The normal polling plan avoids full closed scans entirely.

### 9.3 Recommendation for polling

- **Open sets** (issues, MRs): offset, `per_page=100`, `order_by=created_at&sort=asc` (stable, since new items append at the end), follow `next`.
- **Deltas**: `updated_after=<watermark − 2 min>&order_by=updated_at&sort=asc&per_page=100`. This is usually a single page. Paginate if `x-next-page` is set.
- **Counts**: GraphQL or `issues_statistics` (section 16), never by paging.

---

## 10. Conditional requests (ETag and `If-None-Match`)

Sources: Rails `Rack::ETag` and `Rack::ConditionalGet` middleware (not documented in the GitLab REST docs; the troubleshooting page only defines `304`), **[live]** tests

Observed on GitLab.com for `/issues`, `/issues_statistics`, `/merge_requests`, `/pipelines`, `/pipelines/latest`, `/projects/:id` and `/projects/:id/issues/:iid`, and on the target instance (architecture §9.4):

```http
HTTP/2 200
etag: W/"968043104ac0def579612781096ef93c"
cache-control: max-age=0, private, must-revalidate
vary: Origin
```

```http
GET …/issues?per_page=1
If-None-Match: W/"968043104ac0def579612781096ef93c"

HTTP/2 304
etag: W/"968043104ac0def579612781096ef93c"
x-total: 9518
link: …                     (pagination headers are still sent)
ratelimit-observed: 2       (still counted)
```

| Question | Answer |
|---|---|
| ETag type | Weak, `W/"<32 hex>"` = MD5 of the serialized body. It is generated **after** the response is built. |
| Does `304` save server work? | **No.** x-runtime for 100 open issues was about 1.1 s for `200` and 1.06–1.42 s for `304` **[live]**. |
| Does `304` count against the rate limit? | **Yes** (`RateLimit-Observed` incremented) **[live]**. |
| Reliable? | Yes for "nothing in this response changed". The ETag changes whenever **any** serialized field changes, including avatar cache-busters (`avatar_url?...v=<ts>`), `user_notes_count` and `upvotes`, so expect occasional "spurious" `200`s that contain no relevant change. |
| Format sensitivity | Send it back **byte-for-byte**. The strong form `"…"` (without `W/`) → `200` **[live]**. In .NET, use `request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag))` (keeps `W/` **[.NET]**), or `TryAddWithoutValidation("If-None-Match", etag)`. |
| Per page? | The ETag is per exact URL. Store it per `(resource, full query string, page)`. A page-1 `304` says nothing about page 2. |
| Pagination headers on `304` | Present **[live]**, so `x-total` from a `304` is usable. |
| `Last-Modified` / `If-Modified-Since` | Not sent. Do not use. |
| GraphQL `POST` | It has an ETag, but `POST` is not conditional. Do not rely on it. |

**Design consequence:** ETags reduce bandwidth and client-side parsing, which is fine for a tray app, but they do **not** reduce request count or server load. Rate-limit protection must come from **fewer requests**: deltas, counts, and longer intervals for metadata. Keep architecture §13's rules, but do not count `304`s as "free".

---

## 11. Rate limits

Sources: <https://docs.gitlab.com/administration/settings/user_and_ip_rate_limits/>, <https://docs.gitlab.com/user/gitlab_com/rate_limits/>

### 11.1 Headers

These appear on every response when a `Rack::Attack` throttle is active **[live][docs]**:

```http
ratelimit-name: throttle_authenticated_api      (throttle_unauthenticated_api when anonymous)
ratelimit-limit: 2000                           (per-minute equivalent, even if the period is 1 h)
ratelimit-observed: 1                           (requests counted in this window)
ratelimit-remaining: 1999
ratelimit-reset: 1793821500                     (Unix seconds; rounded to the next minute boundary on .com)
```

On a `429` response:

```http
HTTP/1.1 429 Too Many Requests
Retry-After: 37                                  (seconds)
RateLimit-ResetTime: Wed, 04 Nov 2026 19:46:00 GMT
Content-Type: text/plain

Retry later
```

- The `429` body is **plain text** ("Retry later" by default, and admins can change it), not JSON. Do not try to JSON-parse it.
- The headers reflect only the **most restrictive** `Rack::Attack` throttle. Application-level limits (search, issue creation, the per-endpoint `/projects` limits on .com) can return `429` **without** the informational headers **[docs]**.
- **The target 19.5.1-ee instance sends no `RateLimit-*` headers** (architecture §9.4): the throttles are disabled or the gateway strips them. The client must work without them. Use a self-imposed budget and honour `Retry-After` when it is present. If a `429` arrives without `Retry-After`, back off 60 s and then double.
- `429` → `RateLimited` health. Suspend that **credential** (the rate limit is per user, so per token, across all projects that share it), not just the one project.
- On some older endpoints a `403` can also mean throttled. Distinguish it by the `RateLimit-Remaining: 0` header or a plain-text body.

### 11.2 Defaults

| Instance | Authenticated API | Unauthenticated API |
|---|---|---|
| Self-managed (default **off**; values apply once an admin enables them) | **7,200 per 3,600 s per user** (≈ 120/min) | 3,600 per 3,600 s per IP |
| GitLab.com (in effect) | **2,000/min per user** (all API) | 500/min per IP **[live]** |
| GitLab.com per-endpoint | `GET /projects/:id` 400/min; `/projects` list 2,000 per 10 min; `/groups/:id/projects` 600/min; search 100/min | |
| GitLab.com proposed plan-based (not yet in effect) | Free: 5,000/h sustained, 100/min burst (API); Premium 1,250/min; Ultimate 2,000/min burst | 60/h |

GraphQL `POST /api/graphql` counts against the **same** authenticated API throttle (`ratelimit-name` was `throttle_unauthenticated_api` on an anonymous GraphQL call **[live]**).

---

## 12. Errors

Sources: <https://docs.gitlab.com/api/rest/troubleshooting/>, **[live]**, **[src]**

| Status | Typical cause | Body shape | GitWidget mapping |
|---|---|---|---|
| 400 | Invalid parameter value | `{"error":"order_by does not have a valid value"}` or `{"error":"updated_after is invalid"}` **[live]**. Validation: `{"message":{"field":["msg"]}}`. Missing parameter: `{"message":"400 (Bad request) \"x\" not given"}` | `MalformedOrUnsupported`. This is a programming or version bug. Log the parameter and do not retry. |
| 401 | Missing, invalid, expired or revoked token | `{"message":"401 Unauthorized"}` **[live]**, or `{"error":"invalid_token","error_description":"…"}` **[src]** | `AuthenticationFailed` |
| 403 | Role too low, feature disabled, insufficient scope, `pipelines/latest` with no pipeline | `{"message":"403 Forbidden"}`, or `{"error":"insufficient_scope",…,"scope":"api read_api"}` | `AuthorizationFailed` (with a "needs read_api" hint for `insufficient_scope`); the adapter does not call `latest` |
| 404 | Project missing **or no access** (GitLab hides existence) | `{"message":"404 Project Not Found"}`, `{"message":"404 Milestone Not Found"}`; unrouted → `{"error":"404 Not Found"}` **[live]** | `NotFound`. Use the body to tell an encoding or proxy problem from a missing project. |
| 405 | Non-GET on a moved project path | `{"message":"Non GET methods are not allowed for moved projects"}` | n/a (read-only) |
| 408 | Database statement timeout on heavy list queries (MR docs) | | Transient. Retry with a smaller `per_page`. |
| 409 | Conflict (writes only; 19.5+ concurrent pipeline retry) | `{"message":"…"}` | n/a for GETs |
| 412 | `If-Unmodified-Since` on DELETE | | n/a |
| 422 | Unprocessable (writes) | `{"message":"…"}` or `{"error":"…"}` | n/a for GETs, but treat as `MalformedOrUnsupported` |
| 429 | Rate limit | plain text | `RateLimited` |
| 500 | Server error | `{"message":"500 Internal Server Error"}` | `ProviderError`, retry conservatively |
| 502/503/504 | Puma restart, overload, gateway | Often an **HTML** page | `ProviderError` with transient retry. Never parse HTML as JSON (architecture §9.4). |

Parse error bodies defensively: try `message` (string or object), then `error` and `error_description`, then fall back to the raw text (truncated and sanitized).

---

## 13. Self-managed specifics

Sources: <https://docs.gitlab.com/install/relative_url/>, <https://docs.gitlab.com/api/rest/troubleshooting/>

- **Base URL**: configure the *instance root* (for example `https://git.corp.example/gitlab`) and derive:
  - REST: `{root}/api/v4/` (**with a trailing slash**; build relative paths **without** a leading slash, see 2.1)
  - GraphQL: `{root}/api/graphql`
  - Normalise user input by stripping a trailing `/`, `/api/v4` or `/-/…`.
- **Relative URL root** (`/gitlab`): `web_url` and `Link` headers already include it. Never construct web URLs yourself.
- **`http://` internal hosts**: allowed. Warn in the UI, because the token travels in clear text. Never auto-upgrade or downgrade the scheme on redirects.
- **TLS with a corporate CA**: .NET uses the OS trust store (Windows certificate store, macOS Keychain, and the Linux OpenSSL CA bundle, `/etc/ssl/certs`). If the corporate root CA is installed in the OS, nothing else is needed. Do **not** add an "ignore certificate errors" option in v1. If it is ever needed, make it per-host certificate pinning instead.
- **Proxies**: `HttpClient` uses `HttpClient.DefaultProxy`, which honours `HTTPS_PROXY`, `HTTP_PROXY` and `NO_PROXY` environment variables on all platforms and the system proxy settings on Windows and macOS. Proxy authentication (NTLM or Kerberos) works with `DefaultCredentials` on Windows. Elsewhere it is not reliable, so expose a manual proxy setting as a later enhancement.
- **Reverse proxies** must not decode `%2F`. The diagnostic is in section 2.1. Since adapter requests use numeric IDs after resolution, only the first path lookup is affected.
- **Gateways** (architecture §9.4): configurable `User-Agent`, no auto-redirect, and reject non-JSON `2xx` responses.

---

## 14. Version detection

Source: <https://docs.gitlab.com/api/metadata/>, `lib/api/metadata.rb` **[src]**

```http
GET /api/v4/metadata          (preferred)
GET /api/v4/version           (deprecated since 15.5, still served; same payload)
```

```json
{
  "version": "19.5.1-ee",
  "revision": "abc1234def5",
  "kas": { "enabled": true, "externalUrl": "wss://git.corp.example/-/kubernetes-agent/",
           "externalK8sProxyUrl": "https://git.corp.example/-/kubernetes-agent/k8s-proxy/", "version": "19.5.1" },
  "enterprise": true,
  "plan": null
}
```

| Field | Type | Notes |
|---|---|---|
| `version` | `string` | `MAJOR.MINOR.PATCH[-pre][-ee]`. Parse leniently with a regex `^(\d+)\.(\d+)\.(\d+)`. |
| `revision` | `string` | Git SHA |
| `enterprise` | `bool` | EE codebase (also true for an unlicensed EE install) |
| `plan` | `string?` | GitLab.com namespace plan context. Typically `null` on self-managed. Unverified (needs check on 19.5 EE). |
| `kas` | object | Ignore |

- It **requires authentication**: `401` without a token **[live]**. Allowed scopes are `read_user`, `ai_features` or `ai_workflows` on GET, in addition to the default `api`/`read_api` **[src]**.
- Use it once at connection time to gate features: keyset for issues needs ≥ 18.3, and the MR `draft` filter needs ≥ 19.0. Store the version in diagnostics.

---

## 15. Changes between GitLab 16 and 19.x affecting these endpoints

Sources: <https://docs.gitlab.com/api/rest/deprecations/>, <https://docs.gitlab.com/update/deprecations/>, endpoint pages above, **[live]**

| Version | Change | Impact on GitWidget |
|---|---|---|
| 15.5 | `/version` deprecated → `/metadata` | Use `/metadata`. |
| 15.6 | MR `merge_status` deprecated → `detailed_merge_status` (removal in v5) | Not used. |
| 16.0 | Access tokens **must expire** (max 365 days); `approvals_before_merge` deprecated | Expiry UX (section 1.4) |
| 16.x | Pipeline status `canceling` added | Map it (section 8.3) |
| 17.0 | **Epics REST API deprecated** (removal v5) in favour of work items (GraphQL); child pipelines filterable by `source=parent_pipeline` | Epics are out of scope |
| 17.x | Tasks, incidents, test cases and requirements all appear in the issues REST list; `waiting_for_callback` pipeline status | Filter `issue_type=issue` (3.4) and map the status |
| 17.6 | Admin-configurable token maximum lifetime (400 days behind a flag) | `expires_at` may be further out |
| 17.7 | **Planner** role | Role table (1.2) |
| 17.10 | DPoP for PATs (opt-in) | Error message only |
| 18.3 | **Keyset pagination for project issues**; label `archived` (flag) | Optional full-scan strategy |
| 18.10 | Label `archived` GA | Hide archived labels |
| 19.0 | MR `draft` filter param; `wip` deprecated | Use `draft` |
| 19.x | Issue `web_url` points to `/-/work_items/<iid>` on GitLab.com **[live]** | Treat it as opaque. Unverified (needs check on 19.5 EE). |
| 19.5 | `author.bot` on MRs | Optional "bot" badge |
| still present | `work_in_progress`, `merged_by`, `assignee`, `epic_iid`, `issues_enabled`/`*_enabled` are deprecated but returned **[live][src]** | Do not depend on them |
| planned (v5, no date) | Removal of the deprecated fields above; `milestone=None/Any` wildcards (use `milestone_id`) | Use the current fields now |
| always | Non-numeric IDs on integer path params → `400` | Use numeric IDs for sub-resources |

GraphQL-only data relevant here: work items (incl. OKRs and epics as work items), **type-filtered counts in one call**, **exact MR counts above 10k**, and milestone `stats` (group-wide, see the caveat in section 6).

---

## 16. Recommended polling plan (large project)

Assumptions: one project with about 2,500 open and 7,000 closed issues, about 120 open and 7,000 merged or closed MRs, about 40 labels and 10 active milestones. Issue/MR/pipeline interval 30 s, metadata 5 min, reconciliation 15 min (architecture §11).

### 16.1 Per poll (every 30 s): about 4–5 requests

| # | Request | Purpose |
|---|---|---|
| 1 | `GET /projects/:id/issues?issue_type=issue&state=all&updated_after=<W_i − 120 s>&order_by=updated_at&sort=asc&per_page=100` | Issue delta: created, closed, reopened, relabelled, retitled, reassigned, milestone change. Usually 0–3 items. |
| 2 | `GET /projects/:id/merge_requests?state=all&updated_after=<W_m − 120 s>&order_by=updated_at&sort=asc&per_page=100` | MR delta (opened, merged, closed, draft toggle) |
| 3 | `GET /projects/:id/pipelines?ref=<default_branch>&order_by=id&sort=desc&per_page=20` with `If-None-Match` (as implemented) | Pipeline status changes |
| 4 | `POST /api/graphql` counts query (below) **or** `GET /projects/:id/issues_statistics` | Open and closed totals (and per-milestone counts) |
| (5) | `GET /projects/:id/pipelines/:pid` | Only when a pipeline became terminal, to get `finished_at` |

Watermarks:

- `W_x` is the **maximum `updated_at` seen in accepted data**, from the server clock, not the local clock. The 120 s overlap absorbs clock skew and transaction commit lag. Overlaps are harmless because items are de-duplicated by `id`, and events are deduplicated by key (architecture §14.2).
- Apply deltas to the last accepted snapshot to form the new snapshot. Open counts derived from the merged open set must agree with the count source. **If they disagree, mark the snapshot partial and trigger reconciliation instead of accepting it.**

What `updated_after` does **not** catch, so reconciliation is needed for these:

- deleted issues and MRs (they vanish without an update);
- issues moved out of the project (the original becomes `closed` with `moved_to_id`, which *is* caught) and items converted to another type (they vanish from the `issue_type=issue` view);
- label renames and recolours (the label changes, not the issue: caught by the 5-min label poll);
- milestone renames (the issue's embedded milestone is stale until the issue updates: refresh by milestone `id` from the milestone poll);
- confidentiality or permission changes on the token.

### 16.2 Every 5 minutes (metadata): about 2 + ceil(n/100) requests

| Request | Notes |
|---|---|
| `GET /projects/:id/labels?include_ancestor_groups=true&per_page=100` (no `with_counts`) | Colours and names |
| `GET /projects/:id/milestones?include_ancestors=true&state=active&per_page=100` | Active milestones (dates, state, title) |
| Per-milestone counts | Already in the GraphQL query (#4). With REST only, add `issues_statistics?milestone=<title>` per active milestone. |

### 16.3 Every 15 minutes (reconciliation): about ceil(open/100) + 4 requests

| Request | Notes |
|---|---|
| `GET /projects/:id` | Path, default branch, feature access levels, archived |
| `GET /projects/:id/issues?issue_type=issue&state=opened&order_by=created_at&sort=asc&per_page=100` (all pages) | Full open set (25 pages for 2,500 open). Replaces the open population exactly. |
| `GET /projects/:id/merge_requests?state=opened&order_by=created_at&sort=asc&per_page=100` (all pages) | Full open MR set |
| `GET /projects/:id/milestones?include_ancestors=true&state=closed&per_page=100` | Closed milestones (rarely change) |
| Count query (GraphQL), including closed-milestone counts | |
| `GET /personal_access_tokens/self` (once a day is enough) | Expiry warning |

"Fetch all open items fully" (the user's decision) is satisfied at reconciliation and at startup. Between reconciliations, the delta keeps the open set current. If the open set is small (≤ 500 open issues, ≤ 5 pages), the adapter may fetch it fully on every poll instead of using deltas. That is simpler and costs about 5 extra requests per poll.

### 16.4 Request budget

| Component | Requests/min per project |
|---|---|
| Polls (4 req × 2/min) | 8 |
| Terminal-pipeline detail | ~0.5 |
| Metadata (~3 per 5 min) | 0.6 |
| Reconciliation (~30 per 15 min, for 2,500 open issues) | 2 |
| **Total** | **≈ 11/min** (≈ 660/h) |

- 10 such projects on one token come to about 110/min, which is **near the self-managed default of about 120/min if an admin has enabled the throttle**, and far below GitLab.com's 2,000/min.
- Recommendations:
  - Use a global per-credential token bucket, for example 60/min by default and configurable.
  - Stagger reconciliations.
  - Let the scheduler stretch intervals when the budget is tight, rather than dropping deltas.
- The first sync of a big project costs ceil(open/100) + ~10 requests.

### 16.5 Is GraphQL the better count source? Yes, for counts only.

One query returns every number the widget needs, exactly, issue-type-filtered and project-scoped, regardless of size. Tested **[live]**: `gitlab-org/gitlab` (48k open and 254k closed issues; 213k merged MRs), 1 request, about 1.8 s. REST `X-Total` is missing there, and `issues_statistics` took about 4 s and cannot filter by type.

```graphql
query Counts($path: ID!) {
  project(fullPath: $path) {
    issuesOpen:   issues(state: opened, types: [ISSUE]) { count }
    issuesClosed: issues(state: closed, types: [ISSUE]) { count }
    mrsOpen:      mergeRequests(state: opened) { count }
    mrsMerged:    mergeRequests(state: merged) { count }
    mrsClosed:    mergeRequests(state: closed) { count }
    m501o: issues(state: opened, types: [ISSUE], milestoneTitle: ["v1.2"]) { count }
    m501c: issues(state: closed, types: [ISSUE], milestoneTitle: ["v1.2"]) { count }
  }
}
```

```json
{ "data": { "project": { "issuesOpen": { "count": 2570 }, "issuesClosed": { "count": 6930 },
  "mrsOpen": { "count": 118 }, "mrsMerged": { "count": 5491 }, "mrsClosed": { "count": 1880 },
  "m501o": { "count": 2 }, "m501c": { "count": 0 } } } }
```

Notes:

- Send `POST {root}/api/graphql` with `Content-Type: application/json` and body `{"query": "...", "variables": {"path": "acme/tools/widget"}}`, using the same `PRIVATE-TOKEN` or Bearer header. `read_api` is sufficient for queries.
- Errors come back as **HTTP 200** with an `errors[]` array. Treat any `errors` entry as a failed count fetch, and do not accept partial `data`.
- A `null` project means it was not found or there is no access.
- Query complexity is limited (about 250 for authenticated users). Keep aliases to about 20–30 per query and split per-milestone counts into chunks. Unverified (needs check on 19.5 EE): the exact limit.
- `milestoneTitle` for a group milestone is project-scoped (12 in the project versus 2,594 group-wide for "18.3") **[live]**. Do **not** use `Milestone.stats`.
- Keep REST for all item data (architecture: REST v4 adapter). GraphQL is a narrow, optional count source behind the same `IRepositoryStatusProvider`. If GraphQL is disabled or blocked by the gateway, fall back to `issues_statistics`. Closed and merged MR counts then become "≥ 10,000" when `X-Total` is absent.

---

## 17. Implementation checklist for the GitLab adapter

- `long` for all IDs. `DateTimeOffset` from ISO 8601. `DateOnly` for `due_date`, `start_date` and `expires_at`.
- `System.Text.Json` with unknown-property tolerance. Enums as strings with an `Unknown` fallback (pipeline `status` and `source`, `issue_type`, MR `state`).
- Base URI with a trailing slash. Relative paths without a leading slash. `Uri.EscapeDataString` exactly once. Numeric project ID after resolution.
- `PRIVATE-TOKEN` header. No auto-redirect. Handle `301` → numeric ID explicitly.
- Store ETags per full URL and send them verbatim. A `304` reuses that section, but it still counts toward the budget.
- Missing `X-Total` means unknown, never 0. Stop pagination on an empty `x-next-page`, a missing `rel="next"`, or `[]`.
- Count source: GraphQL, falling back to `issues_statistics`. The open-set size must agree with the open count, or the snapshot is rejected.
- Rate limits: read `RateLimit-*` if present, honour `Retry-After`, budget per credential, and expect the plain-text `429` body.

---

## 18. Open questions to verify on the 19.5.1-ee instance

Run these from the colleague session. Set:

```sh
export GITLAB_URL="https://git.example.corp"          # instance root, incl. relative root if any
export P="group%2Fsubgroup%2Frepo"                     # URL-encoded project path
export PID=1234                                        # numeric project id
export UA="git/2.47.0"                                 # gateway-accepted User-Agent (architecture §9.4)
alias glc='curl -sS -i -A "$UA" -H "PRIVATE-TOKEN: $GITLAB_TOKEN"'
```

1. **Version and metadata fields** (`plan`, `enterprise`):
   `glc "$GITLAB_URL/api/v4/metadata"`
2. **Token self-info works for project or group access tokens; date format of `expires_at`:**
   `glc "$GITLAB_URL/api/v4/personal_access_tokens/self"`
3. **Body and header for an invalid token** (use an obviously fake token, never a real revoked one in logs):
   `curl -sS -i -A "$UA" -H "PRIVATE-TOKEN: glpat-00000000000000000000" "$GITLAB_URL/api/v4/projects/$PID" | sed -n '1,30p'`
   For an expired token, ask an admin or use a test token that expired yesterday, and record the `WWW-Authenticate` and JSON body.
4. **Insufficient scope body** (a token with only `read_user`):
   `curl -sS -i -A "$UA" -H "PRIVATE-TOKEN: $READ_USER_TOKEN" "$GITLAB_URL/api/v4/projects/$PID/issues?per_page=1"`
5. **Rate-limit headers really absent** (also on 429), and the `ETag` and `304` behaviour:
   `glc "$GITLAB_URL/api/v4/projects/$PID/issues?state=opened&per_page=100" -o /dev/null -D - | grep -iE '^(etag|ratelimit|x-total|x-runtime)'`
   `glc -H 'If-None-Match: W/"<etag from above>"' "$GITLAB_URL/api/v4/projects/$PID/issues?state=opened&per_page=100" -o /dev/null -D - | grep -iE '^(HTTP|x-runtime)'`
6. **Which issue types appear unfiltered** (requirements, objectives, key results?):
   `for p in 1 2 3; do glc "$GITLAB_URL/api/v4/projects/$PID/issues?state=all&per_page=100&page=$p" | tail -1 | python3 -c 'import json,sys;[print(i["issue_type"]) for i in json.load(sys.stdin)]'; done | sort | uniq -c`
7. **`issues_statistics` ignores `issue_type`** (the two outputs should be identical):
   `glc "$GITLAB_URL/api/v4/projects/$PID/issues_statistics" | tail -1; glc "$GITLAB_URL/api/v4/projects/$PID/issues_statistics?issue_type=issue" | tail -1`
8. **Issue `web_url` form** (`/-/issues/` or `/-/work_items/`):
   `glc "$GITLAB_URL/api/v4/projects/$PID/issues?per_page=1" | tail -1 | grep -o '"web_url":"[^"]*work_items[^"]*"\|"web_url":"[^"]*issues[^"]*"' | head -1`
9. **Keyset pagination on issues** (expect a `Link` with `cursor=`, no `x-total`); MR keyset ignored:
   `glc "$GITLAB_URL/api/v4/projects/$PID/issues?pagination=keyset&order_by=updated_at&sort=asc&per_page=2" -o /dev/null -D - | grep -iE '^(link|x-total|x-next-page)'`
   `glc "$GITLAB_URL/api/v4/projects/$PID/merge_requests?pagination=keyset&order_by=updated_at&sort=asc&per_page=2" -o /dev/null -D - | grep -iE '^(link|x-total|x-next-page)'`
10. **Feature-disabled response** (pick a project with issues or CI disabled): `403` or `404`, and the body:
    `glc "$GITLAB_URL/api/v4/projects/$OTHER_PID/issues?per_page=1" | sed -n '1p;$p'`
    `glc "$GITLAB_URL/api/v4/projects/$OTHER_PID/pipelines?per_page=1" | sed -n '1p;$p'`
11. **Label `with_counts` scoping for group labels** (compare with project-scoped `issues_statistics`):
    `glc "$GITLAB_URL/api/v4/projects/$PID/labels?with_counts=true&per_page=100" | tail -1 | python3 -c 'import json,sys;[print(l["is_project_label"],l["name"],l.get("open_issues_count"),l.get("closed_issues_count")) for l in json.load(sys.stdin)]'`
    `glc "$GITLAB_URL/api/v4/projects/$PID/issues_statistics?labels=<group-label-name>" | tail -1`
12. **Duplicate milestone titles** (project and group both have the same title): does `milestone=<title>` match both?
    `glc "$GITLAB_URL/api/v4/projects/$PID/milestones?include_ancestors=true&per_page=100" | tail -1 | python3 -c 'import json,sys,collections;c=collections.Counter(m["title"] for m in json.load(sys.stdin));print([t for t,n in c.items() if n>1])'`
13. **Group milestone via the project milestone-issues route:**
    `glc "$GITLAB_URL/api/v4/projects/$PID/milestones/<group_milestone_global_id>/issues?per_page=1" | sed -n '1p;$p'`
14. **GraphQL reachable through the gateway, `read_api` sufficient, and the complexity limit for about 30 aliases:**
    `curl -sS -i -A "$UA" -H "PRIVATE-TOKEN: $GITLAB_TOKEN" -H 'Content-Type: application/json' "$GITLAB_URL/api/graphql" --data '{"query":"{ project(fullPath:\"group/subgroup/repo\") { o: issues(state: opened, types:[ISSUE]) { count } c: issues(state: closed, types:[ISSUE]) { count } mm: mergeRequests(state: merged) { count } } }"}'`
15. **`pipelines/latest` with no pipeline on head** (expect `403`), and the `id` magnitude:
    `glc "$GITLAB_URL/api/v4/projects/$PID/pipelines/latest" | sed -n '1p;$p'`
16. **Token expiry boundary:** with a test token whose `expires_at` is today, check whether it is rejected from 00:00 UTC.
17. **Moved project redirect** (if a renamed project exists): `glc "$GITLAB_URL/api/v4/projects/<old%2Fpath>" | grep -iE '^(HTTP|location)'`
18. **`X-Total` omission threshold** on the largest project (closed or all issues): `glc "$GITLAB_URL/api/v4/projects/$PID/issues?state=all&per_page=1" -o /dev/null -D - | grep -iE '^(x-total|link)'`
19. **`last_activity_at` updates on issue changes** (for poll skipping): read `/projects/$PID`, close or reopen a test issue (manually in the UI, not from the widget), and read it again.

---

## 19. Verified on the target 19.5.1-ee instance (2026-11-04)

Results of read-only checks run from a session on the work network (host redacted as `<base>`). These override "Unverified" notes above for this instance.

| Topic | Result |
|---|---|
| API base | `<base>/api/v4`, no relative URL root; GraphQL at `<base>/api/graphql` |
| TLS | Public CA (SwissSign), no corporate root needed |
| Gateway (Citrix) | Admits any User-Agent **containing** `git/2.47.0` (e.g. `GitWidget/0.1 git/2.47.0` → 200). Other agents (e.g. `curl/8`) get `302 Object Moved`, `text/html`, `Location: https://<portal-host>/cgi/tm?code=…`, empty body. A VPN DNS race can also produce this redirect. |
| GraphQL | Reachable through the gateway with the same User-Agent; `read_api` suffices. `issues(state:…, types:[ISSUE]) { count }` and `issues(milestoneTitle:…)` work (200, exact counts). |
| `issues_statistics` | Same result with and without `issue_type=issue` (project only contains `issue` items). |
| Issue types | All items are `issue_type: "issue"` (`type: "ISSUE"`). |
| Feature flags | `issues/merge_requests/builds_access_level: "enabled"` even though the project has **0 MRs and 0 pipelines** → hide panels by empty data, not by flags. |
| `/merge_requests`, `/pipelines` | `200 []` when empty |
| `/pipelines/latest` | `403 {"message":"403 Forbidden"}` when no pipeline exists → treat as "none" |
| Keyset pagination | Works on issues (`order_by=updated_at`): `Link rel="next"` with `cursor=`, no `X-Total`, no `X-Next-Page` |
| ETag / 304 | Weak ETag, `304` works; the 304 was not faster (single sample) → bandwidth only |
| Rate limits | No `RateLimit-*` headers at all |
| Invalid token | `401`, `application/json`, **no** `WWW-Authenticate`, body `{"message":"401 Unauthorized"}` |
| Missing project | `404 {"message":"404 Project Not Found"}` |
| Issue `web_url` | `<base>/<group>/<project>/-/work_items/<iid>` |
| Token self-info | `/personal_access_tokens/self` keys: id, name, revoked, created_at, description, scopes, user_id, last_used_at, last_used_ips, active, granular, expires_at (`YYYY-MM-DD`) |
| Volume (target project) | ~1,450 issues (~320 open), 30 milestones (18 active), 41 labels, no MRs, no pipelines |

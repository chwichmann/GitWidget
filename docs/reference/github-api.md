# GitHub API reference for GitWidget

Developer reference for the GitHub adapter (architecture §10, §12, §13, §20, §21).
Scope: read-only polling of GitHub.com REST API v3 (plus a small GraphQL use for counts) for issues, pull requests, milestones, labels and GitHub Actions workflow runs, for **large repositories** (tens of thousands of closed items).

- Status: written 2026-11-04. Statements marked **Verified** were checked live with read-only `gh api` calls against public repos (`cli/cli`, `dotnet/runtime`, `torvalds/linux`) on that date. **Unverified** means: not tested live and not explicitly stated in the official docs; treat it as an assumption and cover it with tests or defensive code.
- All JSON examples are synthetic and trimmed to the fields that matter.
- Base URL: `https://api.github.com` (keep it configurable; GHES uses `https://{host}/api/v3` and `https://{host}/api/graphql`).

---

## 0. Gotchas that affect the design (summary)

| # | Gotcha | Consequence for GitWidget |
|---|---|---|
| G1 | `GET /repos/{o}/{r}/issues` returns **issues and PRs**. PR items carry a `pull_request` object **including `merged_at`**, and a `draft` flag. (Verified) | The issues endpoint alone is enough for issue *and* PR state (open / closed / merged / draft). `/pulls` is optional. |
| G2 | For a PR, `id` differs between `/issues` (issue id) and `/pulls` (PR id), but `node_id` (`PR_…`) is identical. (Verified) | Use `node_id` as `ExternalId`. Never mix numeric ids across endpoints. |
| G3 | Milestone `open_issues` / `closed_issues` **include PRs**. (Verified: `dotnet/runtime` milestone `8.0.x` closed 242 = 115 issues + 127 PRs) | Do not show REST milestone counts as issue counts. Use GraphQL `Milestone.issues(states:…){totalCount}` or local counts. |
| G4 | The repo's `open_issues_count` includes PRs. (Verified: 8261 ≈ 7539 issues + 721 PRs) | Do not use it as an issue count. |
| G5 | `/issues` uses **cursor pagination**: `Link` has `rel="next"`/`rel="prev"` only, **no `rel="last"`**, and URLs carry `after=`/`before=` cursors. Offset paging (`page=N` without a cursor) fails with **422** once the offset reaches about 9,900 items. (Verified) | Always follow `rel="next"` URLs. Never synthesize `page=N`. Progress cannot be computed from `last`. |
| G6 | A `304 Not Modified` carries **no `Link` header**. (Verified) | Cache the `next` URL together with the ETag for every page. |
| G7 | A 200 response returns a weak ETag `W/"…"`. The 304 response echoes the strong form `"…"`. Both forms match when sent in `If-None-Match`. (Verified) | Store the ETag from the 200 response. Compare ETags with the `W/` prefix stripped. |
| G8 | An authenticated `304` does **not** consume the primary rate limit. (Verified: `X-RateLimit-Used` unchanged over two 304s; documented) | Conditional requests make quiet repos almost free. |
| G9 | `GET /pulls?sort=updated` defaults to **ascending** (oldest first). `/issues` defaults to `desc`. (Verified) | Always send `direction=desc` explicitly. |
| G10 | Search API: separate bucket of **30 req/min**, no ETag (`Cache-Control: no-cache`), counts lag behind (closed-issue count 66,914 vs 66,971 from GraphQL). (Verified) | Use GraphQL `totalCount` for counts, not Search. |
| G11 | GraphQL count queries cost **1 point** from a separate 5,000 points/h bucket. Nesting `totalCount` under `labels(first:100)` on a huge repo **timed out** with an HTML 502. (Verified) | Batch counts into one query. Page labels in chunks of 25–50. |
| G12 | Issues disabled (`has_issues:false`): the list returns `200 []` (PRs disappear too) and a single issue returns `410 "Issues are disabled for this repo"`. PRs disabled (`has_pull_requests:false`): `/pulls` returns **404**. (Verified on `torvalds/linux`) | Read `has_issues` and `has_pull_requests` from the repo. Do not treat 404 on `/pulls` as "repo not found" when the repo call succeeded. |
| G13 | Numeric ids exceed Int32 (issue id 6,209,691,474; run id 41,899,607,319). (Verified) | Use `long` for every numeric id. |
| G14 | `exclude_pull_requests=true` on `/actions/runs` does **not** filter runs. It only empties each run's `pull_requests` array. (Docs) | Filter by `event` or `head_repository` yourself. |
| G15 | Without `User-Agent` the API answers **403 with an HTML body**, not JSON. (Verified) | Always set `User-Agent`. The error parser must tolerate non-JSON bodies. |
| G16 | The 5,000/h primary limit is **per user**, shared by every token and tool of that user (gh CLI, IDEs, …). (Docs) | Budget for headroom and read `X-RateLimit-Remaining` on every response. |
| G17 | API version `2026-03-10` exists. If the header is omitted, the API uses `2022-11-28`, which reaches end of support on **2028-03-10**. (Verified) | Send `X-GitHub-Api-Version: 2026-03-10`. Use no singular `assignee`, and no `merge_commit_sha`. |
| G18 | `since` is inclusive (`updated_at >= since`) with second precision. (Verified) | Use an overlap window and dedupe by `node_id`. |

---

## 1. Authentication and request headers

### 1.1 Required headers (every request)

```http
GET /repos/{owner}/{repo}/issues HTTP/1.1
Host: api.github.com
Authorization: Bearer <token>             # never log; redact in diagnostics
Accept: application/vnd.github+json
X-GitHub-Api-Version: 2026-03-10
User-Agent: GitWidget/1.0 (+https://github.com/<you>/GitWidget)
If-None-Match: W/"6c15…"                  # when an ETag is cached
```

- `Authorization: Bearer <token>`. `token <token>` also works for PATs, and JWTs require `Bearer`. Basic auth with a password is not supported.
- `X-GitHub-Api-Version`: the supported versions are **`2026-03-10`** (most recent) and `2022-11-28`. **Verified**: `X-GitHub-Api-Version-Selected` comes back in the response.
  - If the header is omitted, the API uses `2022-11-28`, which reaches end of support on 2028-03-10.
  - An unknown version string returns **400** with a body listing the supported versions (verified with `2025-01-01`). The docs say a retired version returns **410**.
  - Breaking changes in `2026-03-10` that touch us:
    - the singular `assignee` is removed from issue and PR payloads (use `assignees[]`);
    - `merge_commit_sha` is removed from PR payloads;
    - `has_downloads` is removed from the repo payload;
    - `GET /rate_limit` drops the top-level `rate` (use `resources.core`).
  - Additive changes (new fields) land in all supported versions, so the parser must ignore unknown fields.
- **The ETag depends on the API version and other request headers.** The same resource returned different ETags under the two versions (verified). `Vary: Accept, Authorization, Cookie, …`. Key the conditional cache by (token identity, API version, URL).
- `User-Agent` is mandatory. Missing or invalid → `403`. Observed: `403` with `text/html` body.
- Do not use an HTTP cache that honours `Cache-Control: private, max-age=60` (present on responses). It would serve data up to 60 s stale at a 30 s poll interval. Implement ETag handling explicitly.

### 1.2 Fine-grained PAT (recommended)

Create at *Settings → Developer settings → Fine-grained tokens*.

- **Resource owner**: exactly one user or organization per token. To watch private repos of several owners, create several tokens. `CredentialReference` is per repository, so this is supported. Organizations may require admin approval. Until approved, the token sees public resources only (status `pending`).
- **Repository access**: "Only select repositories" (recommended) or "All repositories". Every fine-grained token also has **read-only access to all public repositories on GitHub**, so public repos such as `dotnet/runtime` need no extra configuration.
- **Repository permissions (all Read-only)**:

| Permission | Needed for |
|---|---|
| Metadata: Read | `GET /repos/{o}/{r}` (mandatory, auto-selected) |
| Issues: Read | `GET /repos/{o}/{r}/issues`, milestones, labels, GraphQL `issues` |
| Pull requests: Read | `GET /repos/{o}/{r}/pulls`, GraphQL `pullRequests`; milestones/labels also accept it |
| Actions: Read | `GET /repos/{o}/{r}/actions/runs`, `/actions/workflows` |

  Contents, Checks, Commit statuses and all write permissions are **not** needed.
- Expiry: configurable, up to "no expiration" unless an org or enterprise policy restricts it (URL pre-fill accepts 1–366 days). Plan for 401 on expiry. **Unverified**: the documented `GitHub-Authentication-Token-Expiration` response header (expiry timestamp for expiring PATs) could be used for an early warning.
- Fine-grained PATs work with GraphQL and Search. Search results are limited to repos the token can read.
- If a permission is missing, the error is `403 {"message":"Resource not accessible by personal access token"}`. The `X-Accepted-GitHub-Permissions` header lists the required permissions as `scope=level`, comma-separated, with alternatives separated by `;`. For example, `pull_requests=read,contents=read; issues=read,contents=read` means either set is enough.

### 1.3 Classic PAT (fallback)

- Public repos only: no scopes at all.
- Private repos: `repo`, which grants full read **and write**. A read-only scope for private-repo issues, PRs or Actions does not exist. Document this risk to users.
- `read:org` is needed only for org membership or SSO checks (not used).
- For SAML SSO orgs, the classic token must be SSO-authorized after creation. **Unverified**: an unauthorized request may return 403 with an `X-GitHub-SSO: required; url=…` header.
- Response headers `X-OAuth-Scopes` (scopes the token has) and `X-Accepted-OAuth-Scopes` (scopes the endpoint accepts) are present (verified for an OAuth token). They are useful for diagnostics. Do not log the token itself.

### 1.4 Test connection / token validation

Run these in order and stop at the first failure.

| Step | Request | Success | Failure mapping |
|---|---|---|---|
| 1 | `GET /user` | `200` → `login`, `id` (long), `node_id`, `type` | `401 "Bad credentials"` → `AuthenticationFailed` (verified: no `X-RateLimit-*` headers on that 401) |
| 2 | `GET /repos/{owner}/{repo}` | `200` → identity fields below | `404` → `NotFound` (non-existent **or** no access, indistinguishable); `301` → repo renamed or transferred |
| 3 | `GET /repos/{o}/{r}/issues?per_page=1` | `200` | `403 "Resource not accessible…"` → missing *Issues: Read* |
| 4 | `GET /repos/{o}/{r}/pulls?per_page=1` (skip if `has_pull_requests == false`) | `200` | `403` → missing *Pull requests: Read* |
| 5 | `GET /repos/{o}/{r}/actions/runs?per_page=1` | `200` | `403` → missing *Actions: Read* |
| 6 | `GET /rate_limit` | `200` → show remaining | does not count against the primary limit |

**Unverified**: step 1 has not been tested with a fine-grained PAT here (only an OAuth token was available). If `/user` turns out to be restricted, step 2 alone validates the token.

Too many invalid-credential requests in a short time make GitHub reject **all** authentication for that user, even valid tokens, with `403`. After a 401, stop polling and do not retry.

Repository identity fields (`GET /repos/{o}/{r}`):

```json
{
  "id": 210716005,                       // long, stable across rename/transfer -> RepositoryIdentity.ExternalId
  "node_id": "MDEwOlJlcG9zaXRvcnkyMTA3MTYwMDU=", // string; legacy-format global id on old repos
  "full_name": "Owner/Repo",             // canonical casing
  "private": false,
  "visibility": "public",                // public | private | internal
  "archived": false,
  "default_branch": "main",
  "has_issues": true,
  "has_pull_requests": true,             // false -> /pulls returns 404 (verified)
  "open_issues_count": 8261,             // INCLUDES open PRs (verified) - do not display as issues
  "pushed_at": "2026-11-04T19:32:46Z",
  "updated_at": "2026-11-04T19:32:46Z",
  "permissions": { "admin": false, "maintain": false, "push": false, "triage": false, "pull": true }
}
```

Stable ids:

- Repository: use the numeric `id`.
- Issues, PRs, milestones, labels, runs: use `node_id` (string). Prefixes seen: `I_` issue, `PR_` pull request, `LA_` label, `WFR_` workflow run.
- Old objects may still carry legacy base64 ids (`MDU6…`). **Unverified risk**: GitHub has been migrating to "next" global ids. Treat `node_id` as an opaque string and also keep `(kind, number)` as a secondary key for re-matching.

Sources: [getting started](https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api) · [authenticating](https://docs.github.com/en/rest/authentication/authenticating-to-the-rest-api) · [fine-grained permissions](https://docs.github.com/en/rest/authentication/permissions-required-for-fine-grained-personal-access-tokens) · [managing PATs](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/managing-your-personal-access-tokens) · [API versions](https://docs.github.com/en/rest/about-the-rest-api/api-versions) · [breaking changes](https://docs.github.com/en/rest/about-the-rest-api/breaking-changes) · [troubleshooting](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api) · [get a repository](https://docs.github.com/en/rest/repos/repos#get-a-repository)

---

## 2. Issues — `GET /repos/{owner}/{repo}/issues`

Permission: Issues: Read.

### 2.1 Query parameters

| Param | Values | Default | Notes |
|---|---|---|---|
| `state` | `open`, `closed`, `all` | `open` | |
| `sort` | `created`, `updated`, `comments` | `created` | An invalid value is **silently ignored** (verified: 200, not 422) |
| `direction` | `asc`, `desc` | `desc` | Always send it explicitly |
| `since` | ISO 8601 timestamp | – | Items with `updated_at >= since` (inclusive, verified). The documented format is `YYYY-MM-DDTHH:MM:SSZ`. Observed: fractional seconds, `+01:00` offsets and a bare date were accepted; garbage → `422`. Always send `yyyy-MM-ddTHH:mm:ssZ` in UTC. |
| `milestone` | milestone **number**, `*`, `none` | – | Not the title |
| `labels` | comma-separated names | – | AND semantics |
| `assignee` | login, `none`, `*` | – | |
| `type` | issue type name, `*`, `none` | – | Org issue types |
| `creator`, `mentioned` | login | – | |
| `per_page` | 1–100 | 30 | Values above 100 are **silently capped** to 100 (verified) |
| `page` | int | 1 | Only valid for small offsets, see §8 |

### 2.2 Response item (relevant fields)

```json
{
  "id": 6209691474,                     // long; ISSUE id (also for PR items!)
  "node_id": "I_kwDOAAAAAc8AAAABAAAAAA", // string -> ExternalId ("PR_…" for PR items)
  "number": 14940,                      // int
  "title": "Crash on startup",
  "state": "closed",                    // "open" | "closed"
  "state_reason": "not_planned",        // null | "completed" | "not_planned" | "reopened" | "duplicate"
  "html_url": "https://github.com/o/r/issues/14940",
  "user": { "login": "octo", "id": 1, "node_id": "U_…", "type": "User" },  // nullable (deleted user -> "ghost")
  "labels": [ { "id": 8955746444, "node_id": "LA_…", "name": "bug", "color": "d73a4a",
                "description": "Something isn't working", "default": true } ],
  "assignees": [ { "login": "octo", "id": 1, "node_id": "U_…" } ],          // array, may be empty
  "milestone": null,                    // or Milestone object (number, title, state, open_issues, closed_issues, due_on, …)
  "type": null,                         // or { "id", "node_id", "name": "Bug", "description", "color", "is_enabled", … }
  "comments": 1,
  "locked": false,
  "created_at": "2026-11-04T19:18:18Z",
  "updated_at": "2026-11-04T19:24:06Z",
  "closed_at": "2026-11-04T19:24:06Z",  // nullable
  "closed_by": { "login": "github-actions[bot]", "type": "Bot" },          // nullable; present on list items
  "author_association": "NONE",
  "sub_issues_summary": { "total": 0, "completed": 0, "percent_completed": 0 },
  "pull_request": null                  // key ABSENT for issues; object for PRs (see 2.3)
}
```

Notes:

- The `labels` items may in theory be plain strings (schema `oneOf`). Accept both forms.
- Label objects inside issues also carry `archived_at` / `archived_by` (observed, nullable).
- `type` (issue types) is present but `null` when unused. `color` is one of `gray|blue|green|yellow|orange|red|pink|purple` or null.
- Also observed: `issue_field_values` (array), `issue_dependencies_summary`, `pinned_comment`, `reactions`, `body` (large). They can be ignored. **Unverified**: a way to suppress `body` in list responses (no such parameter is known).
- `state_reason` is null for open issues that were never closed. It is `reopened` after a reopen, and for PRs it is typically null.
- **Unverified**: `updated_at` is bumped by comments, label, milestone and assignee changes, close/reopen, and edits. The delta logic depends on this, and the 15-minute reconciliation covers any gaps.

### 2.3 PR items in the issues list

The `pull_request` key marks a PR:

```json
{
  "id": 6210158761,                         // issue id != PR id from /pulls
  "node_id": "PR_kwDOAAAAAc8AAAABAAAAAA",   // identical to /pulls node_id (verified)
  "number": 136978,
  "state": "open",
  "draft": true,                            // present on PR items in /issues (verified)
  "pull_request": {
    "url": "https://api.github.com/repos/o/r/pulls/136978",
    "html_url": "https://github.com/o/r/pull/136978",
    "diff_url": "…", "patch_url": "…",
    "merged_at": null                       // string|null: non-null => merged
  }
}
```

Mapping rule:

- If `pull_request` is present, create a `ChangeRequestSnapshot`.
- `state == closed && pull_request.merged_at != null` → merged.
- `state == closed && merged_at == null` → closed without merge.
- `draft` → `IsDraft`.

Sources: [issues](https://docs.github.com/en/rest/issues/issues#list-repository-issues)

---

## 3. Pull requests — `GET /repos/{owner}/{repo}/pulls`

Permission: Pull requests: Read. If the repo has PRs disabled (`has_pull_requests:false`), the endpoint returns 404 (verified).

| Param | Values | Default |
|---|---|---|
| `state` | `open`, `closed`, `all` | `open` |
| `sort` | `created`, `updated`, `popularity`, `long-running` | `created` |
| `direction` | `asc`, `desc` | `desc` for `created`, otherwise **`asc`** (verified for `updated`) |
| `head` | `user:ref` | – |
| `base` | branch name | – |
| `per_page` | 1–100 | 30 |

- There is **no `since` parameter**. Use `sort=updated&direction=desc` and stop paging when `updated_at` drops below the watermark. Pagination is offset-based with `rel="last"` (verified: `page=577` at 100 per page for closed PRs of `dotnet/runtime`).
- Relevant fields: `id` (long, PR id), `node_id` (`PR_…`), `number`, `state` (`open|closed`), `draft` (bool), `merged_at` (nullable), `closed_at` (nullable), `created_at`, `updated_at`, `title`, `html_url`, `user`, `labels[]`, `assignees[]`, `milestone`, `head.ref`, `head.sha`, `head.repo` (nullable when the fork was deleted), `base.ref`, `requested_reviewers[]`, `auto_merge` (nullable).
- **The boolean `merged` is not in list responses** (verified). Only `GET /pulls/{number}` has `merged`. Derive "merged" from `merged_at != null`.
- `merge_commit_sha` is removed under `2026-03-10` (verified absent).
- Recommendation: **do not poll `/pulls`**. `/issues` (G1) already provides `state`, `draft`, `merged_at`, labels, milestone and assignees for PRs. Use `/pulls` only when branch names or review data become a feature.

Sources: [pulls](https://docs.github.com/en/rest/pulls/pulls#list-pull-requests)

---

## 4. Milestones — `GET /repos/{owner}/{repo}/milestones`

Permission: Issues: Read or Pull requests: Read.

| Param | Values | Default |
|---|---|---|
| `state` | `open`, `closed`, `all` | `open` |
| `sort` | `due_on`, `completeness` | `due_on` |
| `direction` | `asc`, `desc` | `asc` |
| `per_page` | 1–100 | 30 |

Pagination is offset-based with `rel="last"` (verified).

```json
{
  "id": 1234567, "node_id": "MI_…", "number": 130,
  "title": "8.0.x", "description": null,          // description nullable
  "state": "open",                                 // open | closed
  "open_issues": 16, "closed_issues": 242,         // INCLUDE PULL REQUESTS
  "created_at": "2021-01-01T00:00:00Z", "updated_at": "2026-11-04T18:05:57Z",
  "closed_at": null, "due_on": null                // nullable; due_on is a date-time (time part usually 07:00:00Z/08:00:00Z), map to DateOnly
}
```

Milestones have **no start date** on GitHub, so map `StartDate = null`.

### 4.1 Do the counts include PRs? Yes (Verified)

The official docs are silent on this. Live comparison on `dotnet/runtime`:

| Milestone | REST open/closed | Search issues open/closed | Search PRs open/closed | GraphQL issues open/closed |
|---|---|---|---|---|
| 8.0.x | 16 / 242 | 16 / 115 | 0 / 127 | 16 / 115 |
| 10.0.x | 17 / 109 | 12 / 61 | 5 / 48 | 12 / 61 |
| Future | 6065 / 8577 | 6054 / 8424 | 2 / 148 | 6059 / 8425 (+ PRs 2 / 149) |

For the "Future" milestone, REST open (6065) is 4 higher than GraphQL issues + PRs (6061), and REST closed (8577) is 3 higher (8574). **Unverified** cause (hidden or spam items, or cached counters).

### 4.2 Accurate issue-only counts per milestone

- **Open**: count locally from the fully fetched open-issue set (`milestone.number`, excluding PR items). This is exact for our snapshot and costs nothing.
- **Closed**: use GraphQL. One query covers all milestones (cost 1 point, verified):

```graphql
query($o:String!,$n:String!){
  repository(owner:$o,name:$n){
    milestones(first:100, states:[OPEN,CLOSED]){
      pageInfo{hasNextPage endCursor}
      nodes{ id number title
        openIssues:   issues(states:OPEN){totalCount}
        closedIssues: issues(states:CLOSED){totalCount}
        openPRs:      pullRequests(states:OPEN){totalCount} } } } }
```

- **Fallback without GraphQL**: `REST closed_issues` minus the result of `GET /search/issues?q=repo:o/r milestone:"<title>" is:pr is:closed` (Search costs, see §6). This is not recommended.

Sources: [milestones](https://docs.github.com/en/rest/issues/milestones#list-milestones)

---

## 5. Labels — `GET /repos/{owner}/{repo}/labels`

Permission: Issues: Read or Pull requests: Read. `per_page` max is 100, offset-based pagination with `rel="last"` (verified: 322 labels on `dotnet/runtime` → 4 pages).

```json
{ "id": 2341282801, "node_id": "MDU6TGFiZWwyMzQxMjgyODAx", "url": "…",
  "name": "bug", "color": "d73a4a",          // 6 hex chars, NO leading '#'
  "description": "",                         // string, may be "" or null
  "default": true,
  "archived_at": null, "archived_by": null } // observed, nullable (label archiving)
```

- Label identity: `name` is unique per repo (case-insensitive) but renamable. `node_id` is stable, so prefer it.
- `color` lacks the `#` prefix. Prepend it when mapping.

Sources: [labels](https://docs.github.com/en/rest/issues/labels#list-labels-for-a-repository)

---

## 6. Cheap count sources for large repositories

### 6.1 Search API — `GET /search/issues`

```
GET /search/issues?q=repo:OWNER/REPO+is:issue+is:closed&per_page=1
→ { "total_count": 5617, "incomplete_results": false, "items": [ … ] }
```

- Rate limit: a **separate `search` bucket of 30 requests/min** when authenticated (10 unauthenticated). It does not touch `core` (verified: `X-RateLimit-Resource: search`, core `used` unchanged).
- Each query returns **at most 1,000 results** (verified: `rel="last"` capped at page 1000 with `per_page=1`). `total_count` itself is not capped (5,617 returned).
- **No ETag** (`Cache-Control: no-cache`, verified), so every search call counts.
- Queries are rejected with "Validation failed" if they are longer than 256 characters (excluding qualifiers) or use more than 5 AND/OR/NOT operators.
- `incomplete_results: true` means the query hit the timeout. The count may be low.
- Freshness: the search index lags. Observed on `dotnet/runtime`:

| Count | Search | GraphQL |
|---|---|---|
| Open issues | 7,533 | 7,539 |
| Closed issues | 66,914 | 66,971 |

  **Not suitable** for exact counters or change detection.
- Always include `is:issue` or `is:pr`. GitHub App user tokens get 422 without one.
- Search for labels with spaces: `label:"good first issue"`. For milestones use `milestone:"10.0.x"`.

### 6.2 GraphQL — `POST /graphql`

```graphql
query($o:String!,$n:String!){
  rateLimit{ cost remaining resetAt }
  repository(owner:$o,name:$n){
    databaseId
    defaultBranchRef{ name }
    hasIssuesEnabled
    openIssues:   issues(states:OPEN){totalCount}
    closedIssues: issues(states:CLOSED){totalCount}
    openPRs:      pullRequests(states:OPEN){totalCount}
    mergedPRs:    pullRequests(states:MERGED){totalCount}
    closedPRs:    pullRequests(states:CLOSED){totalCount}   # closed WITHOUT merge
  } }
```

Verified result on `dotnet/runtime`: `cost: 1`. GraphQL `issues` excludes PRs.

- Rate limit: a **separate bucket of 5,000 points/h per user** (`X-RateLimit-Resource: graphql`).
  - Cost = (sum of requests implied by every connection's `first`/`last`) ÷ 100, rounded, with a minimum of 1. `totalCount`-only connections are effectively free inside a query.
  - Secondary limit: 2,000 points/min. Every connection needs `first`/`last` (1–100), and a query may touch at most 500,000 nodes.
- Timeouts: anything above 10 s is aborted with **502/504**, and the timeout costs **extra primary points**. Verified: `labels(first:100){ nodes{ issues(states:OPEN){totalCount} } }` on `dotnet/runtime` returned an HTML 502, while `first:50` succeeded at cost 1. Page per-label counts in chunks of ≤ 50 (25 to be safe) and catch non-JSON bodies.
- Errors: HTTP **200** with an `errors[]` array, for example `{"data":{"repository":null},"errors":[{"type":"NOT_FOUND","path":["repository"],"message":"Could not resolve to a Repository with the name 'o/r'."}]}` (verified). Always check `errors` even on 200.
- `rateLimit.resetAt` comes back as an ISO 8601 string (observed `"2026-11-04T20:29:00Z"`).
- No ETag or conditional requests, because it is a POST.
- Per-label counts:
  - Configured labels: `label(name:"bug"){ issues(states:OPEN){totalCount} issues(states:CLOSED){totalCount} }` (aliased).
  - All labels: `repository.issues(states:CLOSED, labels:["x"]){totalCount}`.

### 6.3 Recommendation

| Metric | Best source | Why |
|---|---|---|
| Open issue count | **Local count** of the open set (built from `/issues?state=open`, excluding PR items); cross-check with GraphQL `issues(states:OPEN).totalCount` | We hold all open items anyway, so the count is exact and consistent with the list |
| Closed issue count | **GraphQL** `issues(states:CLOSED){totalCount}` | Exact, 1 point, excludes PRs, separate bucket |
| Per-milestone issue counts | Open: local. Closed: **GraphQL** `milestones{ issues(states:CLOSED){totalCount} }` | REST counts include PRs (G3) |
| Per-label issue counts | Open: local. Closed: **GraphQL** in label pages of ≤ 25–50, or only for configured labels | Avoids timeouts (G11) |
| Open PR count | **Local** (PR items in the open set) or GraphQL `pullRequests(states:OPEN)` | – |
| Merged / closed PR totals | GraphQL `pullRequests(states:MERGED / CLOSED)` | REST has no cheap total |
| Do **not** use | repo `open_issues_count`, REST milestone counts, Search counts (for display) | Include PRs, or stale |

Run the GraphQL count query **only when something changed**: the REST delta request returned 200 with relevant items, a reconciliation ran, or the metadata interval elapsed. Do not run it on every 30-second poll.

Sources: [search](https://docs.github.com/en/rest/search/search#search-issues-and-pull-requests) · [GraphQL limits](https://docs.github.com/en/graphql/overview/rate-limits-and-query-limits-for-the-graphql-api) · [GraphQL Repository object](https://docs.github.com/en/graphql/reference/objects#repository)

---

## 7. GitHub Actions — workflow runs

### 7.1 Endpoints

- `GET /repos/{o}/{r}/actions/runs`: all workflows.
- `GET /repos/{o}/{r}/actions/workflows/{workflow_id|file_name}/runs`: one workflow, same parameters and response.
- `GET /repos/{o}/{r}/actions/workflows`: list workflows. Each workflow has `id`, `name`, `path`, and `state` (`active`, `disabled_manually`, `disabled_inactivity`, …).

Permission: Actions: Read. Classic PAT: `repo` for private repos.

| Param | Notes |
|---|---|
| `branch` | Filters on `head_branch`. Use the repo's `default_branch` |
| `event` | `push`, `pull_request`, `schedule`, `workflow_dispatch`, `merge_group`, … |
| `status` | Filters by status **or** conclusion: `completed, action_required, cancelled, failure, neutral, skipped, stale, success, timed_out, in_progress, queued, requested, waiting, pending` |
| `actor`, `created` (search date syntax, e.g. `>=2026-11-01`), `head_sha`, `check_suite_id` | |
| `exclude_pull_requests` | Only empties the `pull_requests[]` array; **does not filter runs** (G14). Useful to shrink the payload |
| `per_page` | max 100, default 30 |

With any of `actor, branch, check_suite_id, created, event, head_sha, status`, the endpoint returns **at most 1,000 results**. This is irrelevant for "latest runs" use. Pagination is offset-based with `rel="last"` (verified).

### 7.2 Response

```json
{
  "total_count": 2500,
  "workflow_runs": [ {
    "id": 41899607319,                  // long (exceeds Int32!)
    "node_id": "WFR_kwLOAAAAAc8AAAAJwWlFFw",
    "name": "CI",                       // workflow name (string|null)
    "display_title": "Fix null ref in parser", // required string (commit msg / PR title)
    "run_number": 758, "run_attempt": 1,
    "event": "push",
    "status": "completed",              // string|null
    "conclusion": "success",            // string|null; null while not completed
    "workflow_id": 389529352,           // long
    "path": ".github/workflows/ci.yml",
    "head_branch": "main",              // string|null
    "head_sha": "eeb44bc432c0107de002dadd34a61b4e2bd15882",
    "head_repository": { "full_name": "o/r" },  // fork for fork PRs
    "html_url": "https://github.com/o/r/actions/runs/41899607319",
    "created_at": "2026-11-04T19:42:58Z",
    "run_started_at": "2026-11-04T19:42:58Z",   // start of the latest attempt
    "updated_at": "2026-11-04T19:43:10Z",
    "pull_requests": []
  } ]
}
```

- There is no `completed_at` on the run. For completed runs, use `updated_at` as `CompletedAt`. Per-job `completed_at` exists only on `/actions/runs/{id}/jobs`, which costs an extra request.
- A re-run keeps the same `id`, increments `run_attempt`, and resets `run_started_at` and `status`. Include `run_attempt` in the event key so a re-run triggers `BuildStarted` again.

### 7.3 Status and conclusion values

- `status`: `requested`, `queued`, `pending`, `waiting`, `in_progress`, `completed`. The response schema documents no enum. The values come from the filter docs and the Checks API.
- `conclusion`: `success`, `failure`, `neutral`, `cancelled`, `skipped`, `timed_out`, `action_required`, `stale`, `startup_failure`, `null`.
  - `startup_failure` (workflow file invalid, account or billing problems) is not in the REST docs but appears in practice and in GraphQL `CheckConclusionState.STARTUP_FAILURE`.
- Treat unknown values as `Unknown` and log them once.

| status | conclusion | BuildState | Note |
|---|---|---|---|
| `requested`, `queued`, `pending`, `waiting` | null | **Queued** | `waiting` = environment approval / concurrency |
| `in_progress` | null | **Running** | |
| `completed` | `success` | **Succeeded** | |
| `completed` | `neutral` | **Succeeded** | Not a failure; alternatively Skipped (product decision) |
| `completed` | `failure`, `timed_out`, `startup_failure` | **Failed** | |
| `completed` | `cancelled` | **Canceled** | |
| `completed` | `skipped` | **Skipped** | Very common for path- or condition-filtered workflows (52 of 100 recent runs on `dotnet/runtime`) |
| `completed` | `action_required` | **Queued** | Waiting for maintainer approval (fork PRs) |
| `completed` | `stale` | **Unknown** | Check suite marked stale by GitHub |
| any | unrecognized / null on completed | **Unknown** | |

### 7.4 "Current build" selection

1. Get `default_branch` from `GET /repos/{o}/{r}` (or GraphQL `defaultBranchRef.name`). Refresh it on the metadata interval, because it can change.
2. Send `GET /actions/runs?branch={default_branch}&per_page=30&exclude_pull_requests=true` with `If-None-Match`.
3. Group runs by `workflow_id` and take the newest by `created_at` (tie-break on `id`). The repo badge is the worst state across the selected workflows, ignoring `Skipped`.
4. **Unverified**: a fork PR whose head branch is named like the default branch, for example a fork's `main`, also matches `branch=main`. To avoid that:
   - exclude `event == "pull_request"`/`"pull_request_target"`, or
   - require `head_repository.full_name == repo.full_name`.
5. Consider ignoring dynamic workflows, which have `path` values like `dynamic/…` (Dependabot, CodeQL default setup, Copilot). Make this configurable.
6. While runs are in progress, the page-1 ETag changes on almost every poll, because `updated_at` moves. Expect 200s during builds and 304s otherwise.

Sources: [workflow runs](https://docs.github.com/en/rest/actions/workflow-runs#list-workflow-runs-for-a-repository) · [workflows](https://docs.github.com/en/rest/actions/workflows) · [check runs (status/conclusion enums)](https://docs.github.com/en/rest/checks/runs) · [GraphQL CheckConclusionState](https://docs.github.com/en/graphql/reference/enums#checkconclusionstate)

---

## 8. Pagination

### 8.1 Link header format

The header lists comma-separated entries of the form `<url>; rel="name"`. The rel values are `next`, `prev`, `first`, `last`. `prev` and `first` are absent on page 1, and **`last` is omitted when it cannot be calculated**.

Offset style (pulls, milestones, labels, actions runs, search):

```
Link: <https://api.github.com/repositories/210716005/pulls?state=open&per_page=100&page=2>; rel="next",
      <https://api.github.com/repositories/210716005/pulls?state=open&per_page=100&page=8>; rel="last"
```

Cursor style (repository **issues**, verified):

```
Link: <https://api.github.com/repositories/210716005/issues?per_page=100&state=open&page=4&after=Y3Vyc29y…%3D>; rel="next",
      <https://api.github.com/repositories/210716005/issues?per_page=100&state=open&page=2&before=Y3Vyc29y…%3D>; rel="prev"
```

### 8.2 Rules

- Treat `next` URLs as **opaque**. They switch from `/repos/{owner}/{repo}/…` to `/repositories/{id}/…` (verified, same host). Do not rebuild them, and do not inspect them beyond checking the host.
- The issues endpoint **returns 422 for deep offsets**: `"Pagination with the page parameter is not supported for large datasets, please use cursor based pagination (after/before)"`.
  - Observed: `per_page=100&page=99` → 200, `page=100` → 422; `per_page=50&page=199` → 422.
  - Following the cursor `next` link beyond that point works (verified).
- `per_page` max is 100 on all endpoints used here. Larger values are silently capped (verified). The default is 30, so always send `per_page=100` for bulk lists.
- The end of the collection is the absence of `rel="next"`. Do not rely on `rel="last"`, and do not rely on "fewer than per_page items" either, because filtered pages can be short.
- `304` responses contain **no `Link` header** (verified). Persist `{url → etag, nextUrl}` per page.
- Parsing:
  - Split on commas *outside* `<…>`. URLs contain `%2C` but may theoretically contain commas.
  - Match `rel="…"` case-insensitively. A rel value may hold several space-separated names.
  - Tolerate missing quotes.
  - A proven option: a regex `<([^>]+)>\s*;\s*rel="?([^";]+)"?` over the whole header.
- Lists can shift while you page. Items may be skipped or duplicated between pages if something changes mid-pagination. Dedupe by `node_id`. For reconciliation, sort `created asc` so new items append at the end. Any partial pagination failure rejects the whole snapshot (architecture §20).
- Pagination safety cap: abort and report `ProviderError` after a configurable maximum, for example 200 pages = 20,000 items, to avoid runaway loops.

Sources: [pagination](https://docs.github.com/en/rest/using-the-rest-api/using-pagination-in-the-rest-api) · [best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)

---

## 9. Conditional requests

- Most endpoints return `ETag`. Single resources, such as `GET /repos/{o}/{r}`, also return `Last-Modified`. **List endpoints observed here returned no `Last-Modified`**, so use ETags.
- To make a conditional request, send `If-None-Match: <etag>` or `If-Modified-Since: <HTTP-date>`.
  - Unchanged → `304 Not Modified`, empty body, `ETag` echoed, rate-limit headers present.
  - Verified: both `If-None-Match` (lists and repo) and `If-Modified-Since` (repo) produced 304.
- **Rate-limit cost**: an authenticated 304 does **not** count against the primary limit (documented). Verified: two consecutive 304s left `X-RateLimit-Used` / `Remaining` unchanged. Unauthenticated 304s do count.
  - **Unverified**: whether 304s count toward *secondary* limits (points/min). Assume yes and keep polling serial.
- **Weak ETags**: 200 responses carry `W/"<sha256>"`, and the 304 echoes `"<sha256>"` without `W/`. Sending either form matches (verified). Store the value from the latest 200 verbatim and send it back unchanged. When comparing ETags internally, strip `W/`. **Unverified**: the weak/strong difference may come from content encoding (gzip).
- **Stability**: identical repeated requests return identical ETags (verified for labels page 2 and issues page 1). ETags are per exact URL, query string included. A changing `since` value or cursor therefore invalidates them, so the delta request must use a **fixed URL** (§13).
- ETags also vary with `Authorization`, `Accept`, and `X-GitHub-Api-Version` (`Vary` header; version difference verified). Clear the ETag cache on token change or version change. Sending a stale ETag is harmless, because the server answers 200.
- **Pages beyond 1**: page N's ETag only says whether page N changed. With cursor pagination, the URL of page N+1 comes from page N's `Link`, and a 304 does not resend it, so reuse the cached `nextUrl`. If page N returns 200 with a different `next` URL, every later page must be fetched fresh.
- `304` must be modelled as "section unchanged", reusing the stored section (architecture §13). If no stored section exists, for example after a cache reset, drop the ETag and refetch.

Sources: [best practices: conditional requests](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#use-conditional-requests-if-appropriate) · [rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api)

---

## 10. Rate limits

### 10.1 Primary limits (per hour)

| Credential | REST `core` | GraphQL | Search |
|---|---|---|---|
| Unauthenticated (per IP) | 60 | – (auth required) | 10 / min |
| PAT (classic or fine-grained), OAuth user token | **5,000 per user** (shared across all of the user's tokens and tools) | 5,000 points | 30 / min |
| GitHub App installation | ≥ 5,000; + 50 per repo/user beyond 20; max 12,500 (15,000 on GHEC) | | |
| `GITHUB_TOKEN` in Actions | 1,000 per repo | | |

The buckets are independent (`GET /rate_limit` → `resources.core`, `search`, `graphql`, `code_search`, …).

### 10.2 Headers (on every response, including 304)

| Header | Meaning |
|---|---|
| `X-RateLimit-Limit` | Bucket size, for example `5000` |
| `X-RateLimit-Remaining` | Remaining in the window |
| `X-RateLimit-Used` | Used in the window |
| `X-RateLimit-Reset` | Window reset, **UTC epoch seconds** |
| `X-RateLimit-Resource` | `core`, `search`, `graphql`, … |
| `Retry-After` | Seconds; sent on secondary limits (and 429) |

- Values can differ between regions or edges. Observed: the GraphQL `remaining` jumped between calls. The headers on the response are authoritative, not `/rate_limit`.
- A 401 with bad credentials carries **no** rate-limit headers (verified). Do not treat their absence as an error.

### 10.3 Secondary limits

- At most 100 concurrent requests (REST and GraphQL combined).
- At most 900 points/min per REST endpoint (GET = 1 point), and 2,000 points/min for GraphQL.
- At most 90 s of CPU per 60 s of real time.
- About 80 content-creating requests per minute (irrelevant for us).
- GitHub recommends **serial** requests per user. A global concurrency of 2 (architecture §12.1) is acceptable but should not be raised.
- `GET /rate_limit` does not count against the primary limit but can count against secondary limits.

### 10.4 Classifying 403 and 429

Evaluate in order:

1. `429` → rate limited (primary or secondary).
2. `403` or `429` with `X-RateLimit-Remaining: 0` → **primary** limit. Wait until `X-RateLimit-Reset` (+ jitter).
3. `403` or `429` with `Retry-After` → **secondary** limit. Wait `Retry-After` seconds.
4. `403` with body `message` containing `"secondary rate limit"` (case-insensitive) and no `Retry-After` → secondary limit. Wait ≥ 60 s, then back off exponentially and cap the number of attempts.
5. `403` with `message` starting `"API rate limit exceeded"` → primary (fallback in case headers are missing). **Unverified**: exact message text.
6. `403` with `"Resource not accessible by personal access token"` / `"… by integration"`, or an `X-Accepted-GitHub-Permissions` header → `AuthorizationFailed` (missing permission).
7. `403` with an `X-GitHub-SSO` header → `AuthorizationFailed` (SSO authorization required). **Unverified**.
8. `403` with a non-JSON (`text/html`) body → configuration or client error, for example a missing `User-Agent` or a blocked client (verified for missing UA). Map to `ConfigurationError`.
9. Any other `403` → `AuthorizationFailed`. This includes the temporary lockout after repeated bad credentials.

While a limit is active:

- suspend **all** scheduled requests of that bucket, for all repos sharing the credential's user;
- make "Refresh now" respect the wait (architecture §21);
- do not apply the transient retry policy. Continuing to send requests while limited can get the integration banned.

### 10.5 `GET /rate_limit`

```json
{ "resources": {
    "core":    { "limit": 5000, "remaining": 4996, "reset": 1793824139, "used": 4, "resource": "core" },
    "search":  { "limit": 30,   "remaining": 29,   "reset": 1793821927, "used": 1 },
    "graphql": { "limit": 5000, "remaining": 4998, "reset": 1793825340, "used": 2 } } }
```

The top-level `rate` property is removed in `2026-03-10`. Use this endpoint for the diagnostics view.

Sources: [REST rate limits](https://docs.github.com/en/rest/using-the-rest-api/rate-limits-for-the-rest-api) · [rate_limit endpoint](https://docs.github.com/en/rest/rate-limit/rate-limit) · [troubleshooting](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api) · [best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)

---

## 11. Error responses

The body is JSON, except for some edge-level errors that return HTML (G15, GraphQL 502):

```json
{ "message": "Validation Failed",
  "documentation_url": "https://docs.github.com/rest/…",
  "status": "422",                                    // observed: STRING, not number
  "errors": [ { "resource": "Issue", "field": "since", "code": "invalid", "message": "…" } ] }  // optional
```

| Status | Meaning | Health mapping |
|---|---|---|
| 301 / 302 / 307 | Repo renamed or transferred; the issue was transferred (single-issue GET) | Follow, and update the stored owner/name on 301 (see note) |
| 304 | Not modified | Success (unchanged section) |
| 400 | Bad request, for example an unsupported `X-GitHub-Api-Version` (verified) | `ConfigurationError` |
| 401 | `"Bad credentials"`: invalid, expired or revoked token | `AuthenticationFailed`; stop polling |
| 403 | Rate limit **or** permission (see §10.4) | `RateLimited` / `AuthorizationFailed` |
| 404 | Not found **or** no access (private repos look like 404); `/pulls` with PRs disabled | `NotFound`. Hint the user to check token owner and permissions |
| 410 | Gone: issue deleted; single issue on a repo with issues disabled (`"Issues are disabled for this repo"`, verified); retired API version | Remove the item / `ConfigurationError` |
| 422 | Validation failed: bad `since`, deep offset pagination, invalid search query | `ProviderError` (a bug in our request) |
| 429 | Too many requests | `RateLimited` |
| 451 | Unavailable for legal reasons (DMCA, trade controls) | `AuthorizationFailed` / `NotFound` |
| 500 / 502 / 503 / 504 | Server error or timeout (requests over 10 s are aborted) | Transient: 1 / 3 / 8 s + jitter, then back off. Reduce `per_page` on repeated 502s |

Redirect note:

- GitHub says to follow redirects and update stored URLs only on 301.
- **Unverified (.NET)**: `HttpClient` auto-redirect drops the `Authorization` header when following redirects. Private renamed repos would then 404. Set `AllowAutoRedirect = false` on the GitHub handler, follow `Location` manually only when the target host is the configured API host, and re-resolve the repo by `id` (`GET /repositories/{id}`, the form GitHub uses in `Link` URLs).
- Log `X-GitHub-Request-Id` with every error for support.

Sources: [troubleshooting](https://docs.github.com/en/rest/using-the-rest-api/troubleshooting-the-rest-api) · [best practices: redirects](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api#follow-redirects) · [get an issue](https://docs.github.com/en/rest/issues/issues#get-an-issue)

---

## 12. Timestamps and other polling concerns

- **Timestamps** are UTC ISO 8601 with second precision, `YYYY-MM-DDTHH:MM:SSZ`. Parse with `DateTimeOffset.Parse(…, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal)`. `X-RateLimit-Reset` is epoch seconds. `due_on` is a date-time and should map to `DateOnly` using the UTC date. **Unverified**: GitHub may store due dates with a time-zone offset, so check that values do not shift by one day.
- **Clock skew**: compute watermarks from the server's `Date` response header or from the max `updated_at` seen, not from the local clock.
- **`since` delta windows**:
  - `since` is inclusive and many items share a second.
  - Use `watermark = max(updated_at seen) − 2 min overlap` and dedupe by `node_id` + `updated_at`.
  - **Unverified**: `updated_at` is the indexed timestamp and might become visible slightly after the change (eventual consistency). The overlap covers this.
- **Issues endpoint returns PRs**. Split them by the `pull_request` key (§2.3). `issues(states:…)` in GraphQL excludes PRs.
- **Deleted issues** disappear from all lists. No tombstone appears in `/issues?since=…`. Only full reconciliation detects them. `GET /repos/{o}/{r}/issues/{n}` returns **410** (repo readable) or 404.
- **Transferred issues** disappear from the source repo, and no event appears in the delta. Detect them during reconciliation. `GET …/issues/{n}` returns **301** with a `Location` header pointing at the new repo, or 404 if the new repo is not readable.
  - Emit "removed" events, not `IssueClosed`, when an item vanishes without a closed state.
  - Issue numbers are never reused within a repo.
- **Converted to discussion**: the issue disappears from the list, like a transfer. **Unverified**: the status code returned by a single GET for a converted issue (probably 404 or 301).
- **Spam and hidden content**: GitHub may hide spam issues and users, which explains small count discrepancies (§4.1). Bot-closed spam shows `state_reason: not_planned` and labels such as `suspected-spam` (observed on `cli/cli`).
- **Ghost users**: `user` can refer to the deleted-user placeholder `ghost`, so `UserSummary` must tolerate missing fields.
- **Locked items** (`locked`, `active_lock_reason`) can be ignored.
- **Archived repos** (`archived: true`) never change. Reduce polling for them to the metadata interval.
- **Issues or PRs disabled**: see G12. Show "Issues disabled" rather than an error.
- **Large bodies**: `body` is included in list items and dominates payload size. Enable `AutomaticDecompression` (gzip/br).
- **`X-Poll-Interval`**: honour it if present. It is mainly sent by the events endpoints, which we do not use.
- **Events API alternative** (`/repos/{o}/{r}/events`): not recommended. It has 30–300 s latency, at most 300 events / 90 days, and does not cover label edits reliably.

Sources: [getting started: timestamps](https://docs.github.com/en/rest/using-the-rest-api/getting-started-with-the-rest-api) · [get an issue](https://docs.github.com/en/rest/issues/issues#get-an-issue) · [best practices](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api)

---

## 13. Recommended polling plan (large repository)

### 13.1 Requests

The rate-limit bucket is in brackets. Every REST request sends `If-None-Match` when an ETag is cached.

**Every poll** (issue interval, 30 s ± 10 %):

- **A. Change detector + delta** [core]
  `GET /repos/{o}/{r}/issues?state=all&sort=updated&direction=desc&per_page=100`
  The URL is fixed, with no `since`, so the ETag stays usable.
  - `304` → no issue or PR changed. Deletions and transfers are not detected here.
  - `200` → upsert every item with `updated_at >= watermark − overlap`.
  - If the oldest item on the page is still newer than the watermark, follow `rel="next"` until an older item appears (rare).
  - Items that are now closed or merged move out of the open set.
- **B. Builds** [core] (build interval, 30 s)
  `GET /repos/{o}/{r}/actions/runs?branch={default_branch}&per_page=30&exclude_pull_requests=true`
- **C. Counts** [graphql], only when A returned relevant changes (state, milestone or label changed) and at most once per minute
  One query: closed issues, open/merged/closed PR totals, and per-milestone closed issue counts. Cost 1 point.

**Every 5 min** (metadata interval):

- **D.** `GET /repos/{o}/{r}` → `default_branch`, `archived`, `has_issues`, `has_pull_requests`, rename detection.
- **E.** `GET /repos/{o}/{r}/milestones?state=all&per_page=100`, all pages.
- **F.** `GET /repos/{o}/{r}/labels?per_page=100`, all pages.
- **G.** [graphql] Per-label closed counts, in pages of ≤ 25–50 labels or for configured labels only. Optional.

**Every 15 min** (forced reconciliation):

- **H.** `GET /repos/{o}/{r}/issues?state=open&sort=created&direction=asc&per_page=100`, following `rel="next"` to the end and keeping an ETag and cached `nextUrl` per page.
  - Rebuild the open set from it, which covers deletions, transfers and missed deltas.
  - An open item that is missing from the result and absent from the delta must be resolved with `GET …/issues/{n}`. The result is 200 closed, 301 transferred, 410 deleted, or 404.
  - Ascending order keeps the cursors of pages before the first change stable, so those pages can return 304. **Unverified**: how often this applies in practice. Budget H as all-200.
- **I.** GraphQL count query (as C) unconditionally, as a consistency check.

**On startup / first run**: H, D, E, F, C and the build request B (no ETags, so everything costs). For `dotnet/runtime` (≈ 8,260 open issues + PRs, 322 labels) that is about 83 + 1 + 2 + 4 + 1 ≈ **91 core requests**.

### 13.2 Budget (core requests/hour per repo, at a 30 s interval)

| Request | Polls/h | Quiet repo (≤ 100 open, few changes/day) | Busy large repo (e.g. `dotnet/runtime`) |
|---|---|---|---|
| A delta | 120 | ~5 (almost all 304) | ~120 (changes nearly every 30 s) + ~5 extra pages |
| B runs | 120 | ~10 | ~120 (runs always in progress) |
| D repo | 12 | ~2 | ~12 (`pushed_at` changes) |
| E milestones | 12 | ~1 | ≤ 24 (2 pages) |
| F labels | 12 | ~0 | ≤ 48 (4 pages, usually 304) |
| H reconciliation | 4 | ≤ 4 (1 page) | ≤ 332 (83 pages, worst case all 200) |
| Single-issue lookups | – | ~0 | ~5 |
| **Total core/h** | | **~20** | **~660 worst case** (~350 if most reconciliation pages return 304) |
| GraphQL points/h | | ~5 | ~60 (C, capped 1/min) + 4 (I) + ≤ 12 × 13 (G, all labels) ≈ 220; without G ≈ 64 |

### 13.3 Capacity in 5,000/h

Reserve about 20 % for the user's other tools (G16) and for manual refreshes, which leaves about 4,000 core requests/h.

| Scenario | Repos that fit |
|---|---|
| Quiet / small repos only | ~**200** (in practice limited by secondary limits and global concurrency, not the hourly budget) |
| Busy large repos (worst case) | ~**6** |
| Busy large repos (realistic, reconciliation partly 304) | ~**11** |
| Mix of 3 busy large + quiet repos | 3 × 660 = 1,980, leaving ~2,000 → ~100 quiet repos |

GraphQL (5,000 points/h) is not the bottleneck: about 20 busy repos with per-label counts, or 70+ without them.

### 13.4 Levers if the budget gets tight

These are adaptive rules for the scheduler:

1. Stretch the reconciliation (H) for repos with more than 1,000 open items to 30–60 min. It is the dominant cost.
2. Poll builds (B) only on the default branch, and back off to 2 min when the last N polls returned 304.
3. When `X-RateLimit-Remaining` < 20 % of the limit, double all intervals. Below 5 %, keep only A and B for repos visible in the UI.
4. Never use Search for anything polled. Use GraphQL for counts only, triggered by changes.
5. Do not fetch `/pulls`. PR state comes from A and H (G1).

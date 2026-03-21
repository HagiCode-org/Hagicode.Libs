# Skills Online API Reference

This document summarizes the online HTTP endpoints used by `repos/skills`.
It focuses on remote APIs and network endpoints, not local repository scanning.

## Scope

The `skills` CLI uses three different strategies to discover or validate skills:

1. Central search through `skills.sh`
2. Decentralized discovery through `/.well-known/skills/`
3. Direct Git repository cloning and local `SKILL.md` scanning

Only the first two are online skill-discovery interfaces. The remaining endpoints are supporting APIs for telemetry, audit, and GitHub metadata.

## Online API Summary

| Category | Method | Endpoint | Purpose | Used to list skills directly? |
| --- | --- | --- | --- | --- |
| Search | `GET` | `https://skills.sh/api/search?q=<query>&limit=10` | Keyword search across the public skills index | Yes |
| Well-known index | `GET` | `https://<host>/.well-known/skills/index.json` | Discover all skills published by a host | Yes |
| Well-known index with path | `GET` | `https://<host>/<path>/.well-known/skills/index.json` | Discover all skills scoped under a site path | Yes |
| Well-known skill file | `GET` | `https://<host>/.well-known/skills/<skill>/SKILL.md` | Fetch one skill definition | Indirectly |
| Well-known extra files | `GET` | `https://<host>/.well-known/skills/<skill>/<file>` | Fetch supporting files declared by the index | No |
| Audit | `GET` | `https://add-skill.vercel.sh/audit?source=<source>&skills=<csv>` | Fetch skill audit metadata | No |
| Telemetry | `GET` | `https://add-skill.vercel.sh/t?...` | Send anonymous CLI usage events | No |
| GitHub repo metadata | `GET` | `https://api.github.com/repos/<owner>/<repo>` | Detect whether a repository is private | No |
| GitHub tree metadata | `GET` | `https://api.github.com/repos/<ownerRepo>/git/trees/<branch>?recursive=1` | Resolve a folder hash for update checks | No |

## 1. Search API

### Endpoint

```text
GET https://skills.sh/api/search?q=<query>&limit=10
```

### Where it is used

- `repos/skills/src/find.ts`
- `SEARCH_API_BASE` defaults to `https://skills.sh`
- The base URL can be overridden with `SKILLS_API_URL`

### Query parameters

| Name | Required | Description |
| --- | --- | --- |
| `q` | Yes | User search text |
| `limit` | No | Maximum number of results; current code uses `10` |

### Expected response shape

```json
{
  "skills": [
    {
      "id": "find-skills",
      "name": "find-skills",
      "installs": 1234,
      "source": "vercel-labs/agent-skills"
    }
  ]
}
```

### Notes

- This is the only central search API hardcoded in the repository.
- It supports keyword discovery, not full catalog export.
- The CLI sorts returned skills by install count after receiving the response.

## 2. Well-known discovery API

### Endpoints

```text
GET https://<host>/.well-known/skills/index.json
GET https://<host>/<path>/.well-known/skills/index.json
```

### Where it is used

- `repos/skills/src/providers/wellknown.ts`
- This is the fallback online provider for arbitrary HTTP(S) URLs that are not GitHub or GitLab sources.

### Resolution behavior

Given a URL such as `https://example.com/docs`, the provider tries:

```text
https://example.com/docs/.well-known/skills/index.json
https://example.com/.well-known/skills/index.json
```

### Expected index response shape

```json
{
  "skills": [
    {
      "name": "my-skill",
      "description": "Brief description",
      "files": ["SKILL.md", "docs/guide.md"]
    }
  ]
}
```

### Validation rules enforced by the CLI

- `skills` must be an array.
- Every entry must include `name`, `description`, and `files`.
- `files` must include `SKILL.md`.
- File paths cannot start with `/` or `\\` and cannot contain `..`.
- Skill names are expected to be lowercase alphanumeric or hyphenated identifiers.

### Related fetches after index discovery

For each discovered skill, the CLI fetches:

```text
GET https://<host>/.well-known/skills/<skill>/SKILL.md
GET https://<host>/.well-known/skills/<skill>/<file>
```

These requests are derived from the `files` array in `index.json`.

### Notes

- This is the main online API for fetching the full skill list from one host.
- It is decentralized: each publisher hosts its own `index.json`.
- `fetchAllSkills(url)` loads the index first, then fetches each skill folder in parallel.

## 3. Audit API

### Endpoint

```text
GET https://add-skill.vercel.sh/audit?source=<source>&skills=<csv>
```

### Where it is used

- `repos/skills/src/telemetry.ts`
- Called by `fetchAuditData(...)`

### Query parameters

| Name | Required | Description |
| --- | --- | --- |
| `source` | Yes | The normalized skill source identifier |
| `skills` | Yes | Comma-separated skill slugs |

### Expected response shape

```json
{
  "some-source": {
    "some-skill": {
      "risk": "low",
      "alerts": 0,
      "score": 95,
      "analyzedAt": "2026-03-21T00:00:00Z"
    }
  }
}
```

### Notes

- The CLI treats this as optional metadata.
- A timeout or error returns `null` and does not block installation.
- Current timeout is `3000` ms.

## 4. Telemetry API

### Endpoint

```text
GET https://add-skill.vercel.sh/t?...query-string...
```

### Where it is used

- `repos/skills/src/telemetry.ts`
- Called by `track(...)`

### Supported event types in code

- `install`
- `remove`
- `check`
- `update`
- `find`
- `experimental_sync`

### Notes

- This endpoint is fire-and-forget.
- Failures are ignored by the CLI.
- Telemetry can be disabled with `DISABLE_TELEMETRY` or `DO_NOT_TRACK`.

## 5. GitHub repository metadata API

### Endpoint

```text
GET https://api.github.com/repos/<owner>/<repo>
```

### Where it is used

- `repos/skills/src/source-parser.ts`
- Called by `isRepoPrivate(owner, repo)`

### Purpose

- Detect whether a GitHub repository is private.
- The result is advisory; failures return `null`.

## 6. GitHub trees API

### Endpoint

```text
GET https://api.github.com/repos/<ownerRepo>/git/trees/<branch>?recursive=1
```

### Where it is used

- `repos/skills/src/skill-lock.ts`
- Called by `fetchSkillFolderHash(ownerRepo, skillPath, token)`

### Purpose

- Resolve a tree SHA for the skill folder.
- Used for update detection and lock-file integrity checks.

### Request headers used by the CLI

```text
Accept: application/vnd.github.v3+json
User-Agent: skills-cli
Authorization: Bearer <token>   # optional
```

### Notes

- The code checks both `main` and `master`.
- If the skill lives at repository root, the root tree SHA is used.

## 7. Git clone network access

This is not an HTTP API, but it is still an online dependency.

### Where it is used

- `repos/skills/src/git.ts`
- `cloneRepo(url, ref)` performs a shallow clone with `simple-git`

### Purpose

- For GitHub, GitLab, and arbitrary Git URLs, the CLI usually does not call a remote list endpoint.
- Instead, it clones the repository and scans `SKILL.md` files locally.

## Practical conclusions

If the question is "what online interfaces are used to obtain skills?", the answer is:

1. `skills.sh/api/search` for central keyword search
2. `/.well-known/skills/index.json` for per-host skill catalogs

If the question is "what online APIs exist in this codebase besides search?", the answer is:

1. Well-known skill discovery endpoints
2. Audit API on `add-skill.vercel.sh`
3. Telemetry API on `add-skill.vercel.sh`
4. GitHub repository metadata API
5. GitHub trees API

## Source references

- `repos/skills/src/find.ts`
- `repos/skills/src/providers/wellknown.ts`
- `repos/skills/src/telemetry.ts`
- `repos/skills/src/source-parser.ts`
- `repos/skills/src/skill-lock.ts`
- `repos/skills/src/git.ts`

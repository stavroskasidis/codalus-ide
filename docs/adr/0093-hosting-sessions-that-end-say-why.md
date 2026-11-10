# 0093. Hosting sessions that end say why, and sign-ins don't churn tokens

- **Status:** accepted (0.13, 2026-11-10)
- **Date:** 2026-11-10
- **Deciders:** maintainer
- **Amends:** [ADR 0031](0031-git-hosting-and-repository-features.md) (what a 401 means), [ADR 0088](0088-signing-in-from-git-changes.md) (signing in again)

## Context

The maintainer, signed in to GitHub with a device code on one or two computers, was asked to sign in again every so often: "Your GitHub session expired or was revoked." Codalus IDE's GitHub app is an OAuth app, whose tokens don't expire, so GitHub had refused a token, or the IDE had concluded so, and the notification didn't say which:

- any 401 on any request (pull requests, checks, review threads) removed the account at once, with the host's message dropped on the way;
- every **Sign In** made a new token, and the old one was never revoked (revoking an OAuth app's token needs the app's client secret). GitHub keeps ten tokens per user, app and scopes, and revokes the oldest when it makes an eleventh. Each needless sign-in, on any computer or `--user-data-dir` profile, brings another profile's token closer to being revoked, which makes it sign in again, and so on.

## Decision

- **A 401 is checked before it signs the user out.** `HostingProviderBase` asks the provider's `WhoAmIUrl` (GitHub: `/user`) about the session's token once more. Only a second refusal is "sign in again". If the token is still accepted, or it couldn't be checked (network), the request fails as an ordinary error and the session stays.
- **The reason travels to the user.** The session records when the user signed in (`SignedInAt`, kept through refreshes). The sign-out message names the host, the account, how long ago the user signed in and the host's own message ("github.com refused the token of ada, signed in 3 days ago (Bad credentials)."). `HostingService.SignInRequired` carries it, the notification shows it and the Git output logs it.
- **Signing in again keeps a token that still works.** GitHub's device sign-in first asks `WhoAmIUrl` about the stored token; if GitHub accepts it, that account is kept and no new token is made.
- Providers opt in with `WhoAmIUrl`; without it, a 401 signs out as before. GitHub opts in now. `TODO(issue)`: GitLab (`/api/v4/user`) and Azure DevOps (the profile API), with their mock servers in the tests.

## Consequences

- A single bad answer from the host no longer signs the user out; a revoked token still does, on the next request.
- The next time a session ends, its message says whether the host refused the token and how old it was, which tells a revocation by the ten-token limit from a session the IDE lost.
- A refused request costs one more API call (`/user`).

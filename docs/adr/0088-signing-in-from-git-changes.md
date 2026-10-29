# 0088. Signing in to the repository's host from Git Changes

- **Status:** accepted (0.10.0, 2026-10-29)
- **Date:** 2026-10-29
- **Deciders:** maintainer (the decisions of 2026-10-29: sign in inline in Git Changes; the suggestion is dismissed per repository)

## Context

ADR 0087 made Git Changes' **Create a pull request** button lead to signing in. But it led to **Git Hosting Accounts**, a page that lists every provider (GitHub, Azure DevOps, GitLab), when the repository's remote already says which host it is. Nothing offered to sign in until the user pressed a button that needed it.

That page also offered **Sign In** (the OAuth device flow) for every provider. No OAuth app's client id ships yet (ADR 0031), so the button could only fail: "Device sign-in needs the OAuth app's client id (a setting); sign in with a personal access token instead."

## Options

| Question | Options | Verdict |
|---|---|---|
| Where to sign in when the host is known | A focused dialog for that host; the Accounts page showing that host only; inline in Git Changes | **Inline in Git Changes.** The panel under the outgoing and incoming commits names the host and holds its ways in, with no window or page in between. |
| Suggesting before it's needed | Only when a button needs it; a suggestion in Git Changes | **A suggestion in Git Changes**, the same panel collapsed: "This repository is on GitHub (github.com). Sign in to create pull requests and see CI status and work items." with **Sign In** and **×**. |
| What **×** dismisses | That host; every host (a setting); that repository | **That repository, for good.** It is kept in the state folder's `hosting-sign-in-dismissed.json`, by the repository's root. The pull request button and the hosting windows' Sign in still open the panel, since they ask for it. |
| The device code sign-in without an OAuth app | Keep the button and its error; hide it | **Hide it** wherever it shows (the panel and the Accounts page), and don't start it by itself. A provider with `serverSetting` has device sign-in only when its section's `oauthClientId` is set (`github.oauthClientId`…), and on a self-hosted server only when it isn't the setting's default (the public host's app). A provider without one keeps it. |

## Decision

**The panel** (`HostingSignInViewModel`, `GitChangesView`'s `HostingSignInPanel`):
- It shows while the repository's remote is on a provider's host that the user isn't signed in to. That host is `ShellHost`'s suggested provider: a known host, or an Azure DevOps Server found from the remote (ADR 0087). Hosting must be on.
- It binds to the provider's `AccountProviderRow`, the same object as the Accounts page's row, so a sign-in started in either shows in both.
- Expanded, it offers what the host takes:
  - **Sign in with your Windows account** (**Kerberos** elsewhere) on a self-hosted server with `integratedSignIn`;
  - **Sign In with Your Browser** when the device sign-in is set up;
  - a personal access token, with **Create a token**.
- **Create a token** opens the host's page for one (`HostingService.TokenPageUrl`):
  - GitHub's `settings/tokens/new` with the scopes ticked;
  - GitLab's `-/user_settings/personal_access_tokens` with the `api` scope;
  - Azure DevOps' `_usersSettings/tokens` under the remote's organization or collection.
- The line under the token says the scopes it needs.

**Entry points.**
- **Create a pull request** opens the panel instead of the Accounts page. Once the repository matches the host after signing in, the pull request form opens.
- `IHostingActions.SignIn` for the suggested provider opens the panel too. That is the Pull Requests and Work Items windows' Sign in.
- The Accounts page stays for Git → Git Hosting Accounts and for a remote on a host no provider knows.

## Consequences

- One click in Git Changes leads to the right host's sign-in, and a token's page opens with the scopes set.
- **github.com has Codalus IDE's own OAuth app** (registered by the maintainer, 2026-10-29, with device flow on): `github.oauthClientId`'s default is its client id, which isn't a secret (a device flow has none). On a GitHub Enterprise Server that default doesn't count; the provider refuses it there with a message, and the shell hides the button.
- Without an OAuth app, a token is the only way in on gitlab.com and dev.azure.com; the panel leads with it. Their apps (a non-confidential GitLab application with `api`; a public-client Entra app with Azure DevOps' `user_impersonation`) are still `TODO(issue)` from ADR 0031.
- A dismissed repository keeps no suggestion even after a sign-out. The pull request button still leads to signing in.

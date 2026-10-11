# 0031. Git hosting integration, the credential store, askpass and repository features

- **Status:** accepted (M13b plan, 2026-10-11; the maintainer's standing go-ahead to complete every milestone)
- **Date:** 2026-10-11
- **Deciders:** maintainer

## Context

M13a (ADR 0020) covered history, conflicts and operations. Its checkpoint left M13b the rest of M13:
- **Hosting integration** (roadmap D8): GitHub, Azure DevOps and GitLab as built-in plugins, inactive until the user signs in. Sign-in uses the OAuth device flow, and the token lives in the OS credential store. Then: list and check out pull requests, review comments inline in the editor, create a pull request, CI status for the current branch, and issues or work items (`#123` completion in commit messages, and the ones assigned to the user).
- **Publishing** a local repository to a host.
- **An askpass prompt** in the IDE for HTTPS credentials and SSH passphrases, behind the user's credential helper.
- **Repository features:** worktrees, Git LFS awareness (status, locks), and solutions that span several repositories.

The constraints:
- The Security track: **never store a secret ourselves**. Anything stored goes through a `DaedalusStudio.Platform` credential-store abstraction over the OS stores. Every network call is documented and can be turned off.
- ADR 0003: plugin UI is declarative. Rich surfaces (the Test Explorer, ADR 0010) are shell components fed by plugins through proposed APIs. New plugin APIs start as proposed APIs.
- The shell stays Native AOT clean, never blocks the UI thread, and makes everything cancellable.
- Testing uses mock servers only: no real hosting account is ever written to, and no real credential is used.

## Options

| Question | Options | Verdict |
|---|---|---|
| Where the hosting UI lives | Plugin tree and Markdown views; shell components fed by the plugins | **Shell components.** Pull requests, the sign-in dialog, review comments and the commit-message completion need lists, forms and editor adornments that tree views can't express. The plugins own the host APIs, as the test plugin owns test runners. |
| One plugin or three | One `daedalus.hosting` plugin; one per host on a shared library | **One per host** (`daedalus.github`, `daedalus.azuredevops`, `daedalus.gitlab`), as D8 says, each with its own manifest and settings, on a shared `DaedalusStudio.Plugins.Hosting` library. |
| Who stores the token | The shell, passing it in each request; the plugin, through a secrets API | **The plugin, through a proposed `ISecrets`** backed by the shell's credential store (VS Code's `SecretStorage`). The token never crosses the shell's process boundary except when the plugin stores it. Refreshing it is the plugin's business. |
| macOS Keychain access | The `security` command; the legacy `SecKeychain*` functions; `SecItem*` | **`SecItem*`** (the current API), through P/Invoke with Core Foundation dictionaries. `security add-generic-password -w` puts the secret on the command line. |
| Linux secret store | `secret-tool`; libsecret through P/Invoke | **libsecret** (`secret_password_storev_sync` and friends, the non-variadic forms). `secret-tool` is a separate package that many desktops lack. |
| Askpass program | A separate helper executable; the `daedalus` executable in an askpass mode | **`daedalus` itself**, recognized by the environment its git processes inherit. Nothing else to ship, and the Native AOT executable starts fast. |
| The client ids of the OAuth apps | Hard-coded; settings | **Settings with the project's apps as defaults**, so GitHub Enterprise Server, GitLab self-managed, Azure DevOps Server and the mock servers work. The project's OAuth apps aren't registered yet, so the defaults are empty and a personal access token is the fallback in the same dialog. |

## Decision

### The credential store (`DaedalusStudio.Platform`)

- `ICredentialStore`: `GetAsync`, `SetAsync` and `DeleteAsync` of a secret by service and account. `CredentialStore.Create()` picks the OS store:
  - **Windows:** Credential Manager (`CredReadW`, `CredWriteW`, `CredDeleteW`), generic credentials named `Daedalus Studio:<service>:<account>`, persisted for the user on this machine. A secret over Credential Manager's 2,560-byte limit is split across numbered parts.
  - **macOS:** the login Keychain, generic passwords (`kSecClassGenericPassword`) with the service and account attributes, through `SecItemAdd`, `SecItemCopyMatching`, `SecItemUpdate` and `SecItemDelete`.
  - **Linux:** libsecret (`libsecret-1.so.0`), the default collection, with the schema `org.daedalusstudio.Credential` (attributes `service` and `account`).
  - **In memory:** with `DAEDALUS_CREDENTIAL_STORE=memory` (tests, the smoke test), and when the OS store is missing (no libsecret, no Secret Service on D-Bus). The shell then says that a sign-in lasts only for the session. Nothing is ever written to a file.
- Every call runs off the UI thread; an unavailable store raises `CredentialStoreUnavailableException` with the reason.

### Hosting providers: a proposed API, shell components, built-in plugins

- **Manifest:** `contributes.hostingProviders: [{ "id": "github", "label": "GitHub", "hosts": ["github.com"] }]`. The shell knows the providers before any plugin code runs. The activation event `onHostingProvider:<id>` is implicit.
- **Proposed API** (`DaedalusStudio.Plugins.Proposed`, API 0.2.6):
  - `IHosting.RegisterProvider(HostingProvider)`. `HostingProvider` is an abstract class (new members can be added without breaking implementers): repository resolution from a remote URL; the account, sign-in (device flow, with an `IDeviceCodePrompt` the shell displays) and sign-in with a token, sign-out; pull requests (list, details with review threads, checkout instructions, create); branch status; work items (assigned, search); publishing (owners, create a repository).
  - `ISecrets`: `GetAsync`, `StoreAsync`, `DeleteAsync` of the plugin's own secrets, in the shell's credential store under the service `Daedalus Studio` and the account `<plugin id>/<key>`.
- **Wire:** shell → host requests `hosting/*` (each with the provider id) and the notification `hosting/deviceCode` back from the host while a sign-in waits for the user; `hosting/registerProvider`; `secrets/get`, `secrets/store`, `secrets/delete` from the host (docs/plugin-protocol.md).
- **Inactive until sign-in.** The shell activates a provider's plugin only when the user signs in, opens a hosting window, or opens a repository with a remote on a host where the user is signed in. The non-secret list of accounts (provider, account name, host) is kept in the state folder (`hosting-accounts.json`). Without an account nothing loads and nothing is sent.
- **Shell services:** `HostingService` (workbench) keeps the providers, the accounts, and the repository's match: the first remote (`origin` first) that a signed-in provider resolves.

### Sign-in

- **Device flow:** the provider asks its host for a device code (GitHub `/login/device/code`, GitLab `/oauth/authorize_device`, Microsoft Entra ID `/oauth2/v2.0/devicecode` for Azure DevOps) and the shell shows the code with *Copy Code and Open Browser*. The provider polls at the interval its host asks for (and slower on `slow_down`) until the user approves, declines or the code expires. Cancel cancels the request.
- **Client ids** are settings (`github.oauthClientId`, `gitlab.oauthClientId`, `azureDevOps.oauthClientId`). `TODO(issue)`: register the project's OAuth apps on GitHub, GitLab and Entra ID and make them the defaults.
- **Personal access token:** the same dialog takes a token, which the provider checks (the current user) before storing.
- **Tokens** are stored by the plugin through `ISecrets`. GitLab and Entra ID tokens expire: their refresh tokens are stored too, and refreshed a minute before expiry.
- **Servers** are settings: `github.serverUrl`, `gitlab.serverUrl`, and `azureDevOps.serverUrl` with `azureDevOps.authorityUrl` and `azureDevOps.profileUrl`. The mock servers in the tests and the smoke test use them.

### Pull requests, review comments, CI status, work items, publishing

- **Pull Requests window** (shell tool window): the repository's open pull requests, filtered to all, mine, or those that ask for my review; details with the description, the branches and the review threads; Check Out, Open in Browser, Create Pull Request. Without a signed-in provider it offers Sign In.
- **Check out:** the provider returns a remote (a configured remote or a fork's URL), a refspec and a local branch name. `GitService.CheckoutFetchedBranchAsync` fetches it and switches to the branch, creating it (and its upstream, for a branch of the same repository) or fast-forwarding it.
- **Review comments:** when the current branch has an open pull request, its unresolved threads appear at their lines in open editors: a note at the end of the line with the author and the first words, and the whole thread in a flyout. `hosting.reviewComments` turns them off; resolved threads stay in the Pull Requests window.
- **Create Pull Request** (a document): title, description, the base branch (the default branch first), draft. An unpublished branch is pushed first.
- **CI status:** GitHub check runs and commit statuses, Azure Pipelines builds of the branch, GitLab's latest pipeline and its jobs. Shown in the status bar and Git Changes; refreshed on a branch or HEAD change, after a push, and every `hosting.statusRefreshSeconds` (120) while signed in.
- **Work items:** `#` in the commit message completes from the provider's search (the number and the title); the Work Items window lists the ones assigned to the user.
- **Publish:** *Publish to GitHub/Azure DevOps/GitLab* asks for the owner (the user, an organization or a group; an Azure DevOps project), the name, the visibility and a description; the provider creates the repository; the shell adds it as `origin` (or another free name) and pushes the current branch with `-u`.
- **Turning it off:** `hosting.enabled` (default on) stops every hosting call; signing out deletes the stored secrets.

### Askpass

- `GitService` runs git with `GIT_ASKPASS` and `SSH_ASKPASS` set to the `daedalus` executable, `SSH_ASKPASS_REQUIRE=force`, and `DAEDALUS_ASKPASS_PIPE` and `DAEDALUS_ASKPASS_TOKEN` naming this shell's askpass pipe and a random token.
- Git asks the credential helpers (Git Credential Manager) first, so askpass is only reached when none answers. ssh asks for passphrases and host keys through it.
- `daedalus` started with those variables and one argument (the prompt) sends the prompt over the pipe, prints the answer, and exits 0, or 1 when cancelled. The shell shows the prompt in a dialog, masking passwords and passphrases. The answer goes to git and nowhere else: nothing is stored, and git's own `credential approve` reaches only the user's helper.

### Repository features

- **Worktrees:** `git worktree list --porcelain`, add (a new or an existing branch), remove (forced only after a confirmation for a dirty one), prune, lock and unlock. The Worktrees window lists them; Open opens a worktree's solution in this window, Open in New Window starts another `daedalus`.
- **Git LFS:** the patterns come from `git check-attr filter`, so it works without git-lfs installed. A tracked file whose working copy is a pointer (`version https://git-lfs.github.com/spec/v1`) is *not downloaded*. With git-lfs installed, `git lfs locks --json` lists the locks; Lock and Unlock act on the active file. Git Changes marks LFS files and locks.
- **Several repositories:** the repositories of the solution's folder and of every project folder are found with `rev-parse --show-toplevel`. Git Changes gets a repository picker when there are several, and the status bar names the active one. Gutter markers and blame use the file's own repository.

## Consequences

- The three hosts work the same way in the IDE, and a third-party plugin can add another host (Bitbucket, Gitea) with the same proposed API.
- No new package dependencies: the OS stores are reached through P/Invoke, the hosts through `HttpClient` and `System.Text.Json`.
- Mock servers stand in for every host API in the tests. Real hosts are only reached by users; the API shapes follow the hosts' documented REST and GraphQL APIs.
- `TODO(issue)`:
  - register the OAuth apps and set the client-id defaults;
  - a general authentication-provider API, so other plugins can ask for a GitHub or Microsoft session;
  - the SCM provider API (Git on it), roadmap M22;
  - replying to and resolving review comments, approving, and a whole-pull-request diff;
  - browsing the user's repositories in Clone;
  - validating GitHub Enterprise Server, GitLab self-managed and Azure DevOps Server against real servers.

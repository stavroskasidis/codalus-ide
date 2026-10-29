# 0087. Azure DevOps Server, and Create Pull Request before signing in

- **Status:** accepted (0.10.0, 2026-10-29); where the button signs in is [ADR 0088](0088-signing-in-from-git-changes.md)'s
- **Date:** 2026-10-29
- **Deciders:** maintainer (the decisions of 2026-10-29: the server comes from the remote and is saved when you sign in; a personal access token and the operating system's account; one Azure DevOps server at a time)

## Context

Git Changes has a **Create a pull request** button (`git.createPullRequest`). Until now its enablement was `gitRepository && hostingRepository`, so before you signed in to the repository's host the click only said "not available right now" in the status bar. `CreatePullRequestAsync` already opened the Accounts page for the suggested provider, but the enablement stopped the click before it got there.

Since M13b (ADR 0031) Azure DevOps Server has been "supported through settings": `azureDevOps.serverUrl` points the provider at it. But nobody had used it against a real server, and it fell short in five ways:
- signing in read the profile service (`app.vssps.visualstudio.com`), which only Azure DevOps Services has;
- every call asked for REST API 7.1, which Azure DevOps Server 2020 and 2019 refuse with a 400 ("The latest REST API version this server supports is 6.0.");
- device sign-in is Microsoft Entra ID's, which a server in a domain doesn't use, and many such users sign in with their Windows account rather than a token;
- Publish listed the organizations through the profile service;
- a repository named after its project (`…/{collection}/_git/{repository}`) and an SSH remote on a server whose web port isn't 443 didn't resolve.

You also had to know to set `azureDevOps.serverUrl` at all: a remote on `tfs.contoso.com` matched no provider, so nothing offered to sign in.

Constraints: nothing is sent to a host until the user signs in (ADR 0031). Tokens live only in the OS credential store (`SecretStorage`). The plugin API is a public contract (ADR 0003, 0058). Hosting plugins activate only when they're needed.

## Options

| Question | Options | Verdict |
|---|---|---|
| The button before signing in | Grey it out with a reason; run it and open the sign-in | **Run it and open the sign-in.** The enablement becomes `gitRepository`. On a known host the button opens the Accounts page for its provider. On a detected Azure DevOps Server it opens the page on that server. On an unknown host it opens the page with a message, and with no remote it says to publish first. |
| Which server | Detect it from the remote and save it on sign-in; the setting only, with a hint; detect and ask before saving | **Detect and save on sign-in.** An Azure DevOps Server's remotes say what they are: `…/{collection}/{project}/_git/{repository}`. The server is whatever comes before the collection. Signing in from that row writes `azureDevOps.serverUrl` first. Nothing is written or sent until the user signs in. |
| How to sign in to a server | A personal access token only; a token or the operating system's account | **Both.** The token is sent as Basic, as it is on Azure DevOps Services. **Sign in with your Windows account** (**Sign in with Kerberos** on macOS and Linux) uses Negotiate with the current user's credentials. No token is stored: the session's scheme is `negotiate`. |
| Several Azure DevOps servers at once | One at a time; a sign-in per server | **One at a time.** Sessions are one per provider (ADR 0031). Signing in to a detected server while signed in to another asks you to sign out first. Several servers at once would change how every provider keeps its sessions. |
| The API version | Pin 5.0 for servers; a setting; ask for 7.1, then use what the server says | **7.1, then what the server says.** A 400 that names the latest version it supports is asked again once at that version, and the provider keeps it. No setting, and Azure DevOps Services keeps 7.1. |

## Decision

**The plugin API (1.2.0, additive).**
- `HostingProvider.SignInIntegratedAsync`: signs in as the operating system's user.
- The manifest's `hostingProviders[].integratedSignIn`.
- The protocol's `hosting/signIn` takes `integrated`.

The shell offers the button when the provider sets `integratedSignIn` and its server is self-hosted: the host in `serverSetting` isn't one of the provider's `hosts`, or a server was detected from the remote. That provider's device sign-in is hidden then.

**Credentials stay on the server.** `HostingHttp` has a second client with the default credentials. A request uses it only when its option names the session's host, and the URL's host must match that host or the request fails. The client that talks to every other host never answers a Negotiate challenge.

**The Azure DevOps provider on a server.** It is on a server when `azureDevOps.serverUrl`'s host isn't `dev.azure.com` or `*.visualstudio.com`, and `azureDevOps.profileUrl` is still the default. Then:
- `_apis/connectionData` says who is signed in;
- `_apis/projectCollections` lists the owners for Publish;
- `{collection}/_git/{repository}` resolves as the project's namesake;
- an SSH remote's host matches the server's host without its port.

**Detection.** `HostingService.SuggestServerFor` looks at remotes whose host no provider serves. For an Azure DevOps Server's path, it returns the `azure-devops` provider and the server URL (`AzureDevOpsServerOf`). With two segments before `_git`, a first `tfs` is the server's path (an upgraded Team Foundation Server). Otherwise the segments are the collection and the project. The shell treats the result as the suggested provider, so the Pull Requests and Work Items windows' **Sign in** use it too.

## Consequences

- On a repository on Azure DevOps Server, the first click of **Create a pull request** leads to signing in, and the second opens the form. No setting needs to be known.
- Windows authentication works with no token in the credential store. On macOS and Linux it needs a Kerberos ticket (`kinit`); NTLM there isn't supported.
- A sign-in from a detected server changes `azureDevOps.serverUrl`, so an Azure DevOps Services sign-in is replaced, not kept beside it.
- `TODO(issue)`, deferred:
  - a sign-in per Azure DevOps server (and per GitHub Enterprise or GitLab server);
  - detecting GitHub Enterprise Server and self-managed GitLab from their remotes, whose URLs don't say what they are;
  - validating against a real Azure DevOps Server (2020 and 2022) on Windows, in a domain.
- Revisit if the `tfs` rule picks the wrong server for a real layout. The setting can always be corrected by hand.

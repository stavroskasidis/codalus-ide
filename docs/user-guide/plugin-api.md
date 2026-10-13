# Plugin API reference

The plugin API, version 1.0.0: every public type and member of `Codalus.Plugins.Abstractions`, from its documentation comments. How to write a plugin is in the [plugin guide](../plugins.md); this page is generated from the API and checked by its tests, so it matches the version it names.

## Codalus.Plugins

- [`AuthenticationSession`](#authenticationsession)
- [`BranchStatus`](#branchstatus)
- [`BuildFinishedEventArgs`](#buildfinishedeventargs)
- [`BuildResult`](#buildresult)
- [`CommandInvocation`](#commandinvocation)
- [`CoverageReport`](#coveragereport)
- [`CustomEditorDocument`](#customeditordocument)
- [`DebugLaunchOptions`](#debuglaunchoptions)
- [`DebugValue`](#debugvalue)
- [`DebugVisualizerOptions`](#debugvisualizeroptions)
- [`DeviceCode`](#devicecode)
- [`DirectoryEntry`](#directoryentry)
- [`DocumentTestCandidates`](#documenttestcandidates)
- [`DocumentsChangedEventArgs`](#documentschangedeventargs)
- [`FileCoverage`](#filecoverage)
- [`FileStat`](#filestat)
- [`HostingAccount`](#hostingaccount)
- [`HostingAuthenticationException`](#hostingauthenticationexception)
- [`HostingOwner`](#hostingowner)
- [`HostingProvider`](#hostingprovider)
- [`HostingRepository`](#hostingrepository)
- [`HostingRepositoryInfo`](#hostingrepositoryinfo)
- [`IAuthentication`](#iauthentication)
- [`IAuthenticationProvider`](#iauthenticationprovider)
- [`IBuild`](#ibuild)
- [`ICodalusPlugin`](#icodalusplugin)
- [`ICommands`](#icommands)
- [`ICustomEditorProvider`](#icustomeditorprovider)
- [`IDebug`](#idebug)
- [`IDebugSession`](#idebugsession)
- [`IDebugVisualizerContext`](#idebugvisualizercontext)
- [`IDeviceCodePrompt`](#idevicecodeprompt)
- [`IFileSystemProvider`](#ifilesystemprovider)
- [`IFileSystems`](#ifilesystems)
- [`IHosting`](#ihosting)
- [`IOutputChannel`](#ioutputchannel)
- [`IPluginContext`](#iplugincontext)
- [`IPluginLog`](#ipluginlog)
- [`ISecrets`](#isecrets)
- [`ISettings`](#isettings)
- [`ISourceControl`](#isourcecontrol)
- [`ISourceControlProvider`](#isourcecontrolprovider)
- [`IStatusBarItem`](#istatusbaritem)
- [`ITerminal`](#iterminal)
- [`ITerminals`](#iterminals)
- [`ITestController`](#itestcontroller)
- [`ITestRun`](#itestrun)
- [`ITestRunProfile`](#itestrunprofile)
- [`ITests`](#itests)
- [`ITreeDataProvider<T>`](#itreedataprovidert)
- [`IWindow`](#iwindow)
- [`IWorkspace`](#iworkspace)
- [`InputBoxOptions`](#inputboxoptions)
- [`LineCoverage`](#linecoverage)
- [`MethodCoverage`](#methodcoverage)
- [`MethodOutline`](#methodoutline)
- [`NewPullRequest`](#newpullrequest)
- [`NewRepository`](#newrepository)
- [`NotificationSeverity`](#notificationseverity)
- [`ParameterOutline`](#parameteroutline)
- [`PluginApi`](#pluginapi)
- [`PluginInfo`](#plugininfo)
- [`PluginLocalization`](#pluginlocalization)
- [`PluginLogExtensions`](#pluginlogextensions)
- [`PluginLogLevel`](#pluginloglevel)
- [`ProgressReport`](#progressreport)
- [`ProjectOutput`](#projectoutput)
- [`PublishedRepository`](#publishedrepository)
- [`PullRequest`](#pullrequest)
- [`PullRequestCheckout`](#pullrequestcheckout)
- [`PullRequestDetails`](#pullrequestdetails)
- [`PullRequestFilter`](#pullrequestfilter)
- [`QuickPickItem`](#quickpickitem)
- [`ReviewComment`](#reviewcomment)
- [`ReviewThread`](#reviewthread)
- [`SettingsChangedEventArgs`](#settingschangedeventargs)
- [`SourceControlGroup`](#sourcecontrolgroup)
- [`SourceControlResource`](#sourcecontrolresource)
- [`SourceControlState`](#sourcecontrolstate)
- [`StatusBarAlignment`](#statusbaralignment)
- [`StatusCheck`](#statuscheck)
- [`TerminalOptions`](#terminaloptions)
- [`TerminalOutputEventArgs`](#terminaloutputeventargs)
- [`TestAttachment`](#testattachment)
- [`TestCandidate`](#testcandidate)
- [`TestItem`](#testitem)
- [`TestItemCollection`](#testitemcollection)
- [`TestItemCollectionChangedEventArgs`](#testitemcollectionchangedeventargs)
- [`TestMessage`](#testmessage)
- [`TestRange`](#testrange)
- [`TestRunProfileKind`](#testrunprofilekind)
- [`TestRunRequest`](#testrunrequest)
- [`TestTrait`](#testtrait)
- [`TreeDataChangedEventArgs<T>`](#treedatachangedeventargst)
- [`TreeItem`](#treeitem)
- [`TreeItemCollapsibleState`](#treeitemcollapsiblestate)
- [`TreeItemCommand`](#treeitemcommand)
- [`TypeOutline`](#typeoutline)
- [`ViewEvent`](#viewevent)
- [`ViewNode`](#viewnode)
- [`VisualizerView`](#visualizerview)
- [`WorkItem`](#workitem)
- [`WorkItemQuery`](#workitemquery)
- [`WorkspacePackage`](#workspacepackage)
- [`WorkspaceProject`](#workspaceproject)
- [`WorkspaceSolution`](#workspacesolution)

## Codalus.Plugins.Proposed

- [`IProposedApi`](#iproposedapi)
- [`IProposedApiProvider`](#iproposedapiprovider)
- [`ProposedApiExtensions`](#proposedapiextensions)

### `AuthenticationSession`

`record Codalus.Plugins.AuthenticationSession`

A signed-in session of an authentication provider.

| Member | Description |
|---|---|
| `AuthenticationSession(string Id, string AccessToken, string Account, IReadOnlyList<string> Scopes)` | A signed-in session of an authentication provider. |
| `string AccessToken { get; init; }` | The token to call the service with. |
| `string Account { get; init; }` | Who is signed in, as the user knows the account. |
| `string Id { get; init; }` | The session's id within its provider. |
| `IReadOnlyList<string> Scopes { get; init; }` | What the token allows. |

### `BranchStatus`

`record Codalus.Plugins.BranchStatus`

The CI status of a branch: `BranchStatus.State` is `success`, `failure`, `pending` or `none`, the summary of its checks.

| Member | Description |
|---|---|
| `BranchStatus(string State, IReadOnlyList<StatusCheck> Checks)` | The CI status of a branch: `BranchStatus.State` is `success`, `failure`, `pending` or `none`, the summary of its checks. |
| `IReadOnlyList<StatusCheck> Checks { get; init; }` | The checks, pipelines or builds. |
| `string State { get; init; }` | The overall state. |
| `string? Url { get; init; }` | Where to see it all. |

### `BuildFinishedEventArgs`

`sealed class Codalus.Plugins.BuildFinishedEventArgs` : EventArgs

Arguments of `IBuild.Finished`. `BuildFinishedEventArgs.Project` is null for a solution build.

| Member | Description |
|---|---|
| `BuildFinishedEventArgs(string target, string? project, BuildResult result)` | Arguments of `IBuild.Finished`. `BuildFinishedEventArgs.Project` is null for a solution build. |
| `string? Project { get; }` | The project built, or null for the solution. |
| `BuildResult Result { get; }` | How it ended. |
| `string Target { get; }` | `build`, `rebuild` or `clean`. |

### `BuildResult`

`record Codalus.Plugins.BuildResult`

How a build ended.

| Member | Description |
|---|---|
| `BuildResult(bool Succeeded, bool Canceled, int Errors, int Warnings)` | How a build ended. |
| `bool Canceled { get; init; }` | The build was cancelled. |
| `int Errors { get; init; }` | The number of errors. |
| `bool Succeeded { get; init; }` | No errors, and not cancelled. |
| `int Warnings { get; init; }` | The number of warnings. |

### `CommandInvocation`

`sealed class Codalus.Plugins.CommandInvocation`

The arguments a command was invoked with (JSON values, so commands work across processes).

| Member | Description |
|---|---|
| `CommandInvocation(IReadOnlyList<JsonElement> args)` | The arguments a command was invoked with (JSON values, so commands work across processes). |
| `IReadOnlyList<JsonElement> Args { get; }` | The raw JSON arguments. |
| `T Arg<T>(int index, T fallback = null)` | Deserializes argument `index`, or returns `fallback` when absent or null. |

### `CoverageReport`

`record Codalus.Plugins.CoverageReport`

Coverage reported by a run: per document line, and per method for the results tree.

| Member | Description |
|---|---|
| `CoverageReport(IReadOnlyList<FileCoverage> Files, IReadOnlyList<MethodCoverage> Methods)` | Coverage reported by a run: per document line, and per method for the results tree. |
| `IReadOnlyList<FileCoverage> Files { get; init; }` | Line coverage per document. |
| `IReadOnlyList<MethodCoverage> Methods { get; init; }` | Coverage per method. |

### `CustomEditorDocument`

`record Codalus.Plugins.CustomEditorDocument`

A file opened in a plugin's custom editor.

| Member | Description |
|---|---|
| `CustomEditorDocument(string ViewId, string Path, string Text)` | A file opened in a plugin's custom editor. |
| `string Path { get; init; }` | The file's path. |
| `string Text { get; init; }` | The file's text when it opened. |
| `string ViewId { get; init; }` | The declarative view to fill, and the id the editor's calls use. |

### `DebugLaunchOptions`

`record Codalus.Plugins.DebugLaunchOptions`

How to start a program. `DebugLaunchOptions.Arguments` are passed as they are (no shell parsing).

| Member | Description |
|---|---|
| `DebugLaunchOptions(string Program, IReadOnlyList<string> Arguments, string WorkingDirectory)` | How to start a program. `DebugLaunchOptions.Arguments` are passed as they are (no shell parsing). |
| `IReadOnlyList<string> Arguments { get; init; }` | Its arguments, passed as they are. |
| `IReadOnlyDictionary<string, string>? Environment { get; init; }` | Environment variables added to the shell's environment. |
| `string Program { get; init; }` | The program to start. |
| `string WorkingDirectory { get; init; }` | Its working directory. |

### `DebugValue`

`record Codalus.Plugins.DebugValue`

An evaluated value.

| Member | Description |
|---|---|
| `DebugValue(string Value, string? Type)` | An evaluated value. |
| `string? Type { get; init; }` | Its type, when the debugger knows it. |
| `string Value { get; init; }` | The value as the debugger shows it. |

### `DebugVisualizerOptions`

`record Codalus.Plugins.DebugVisualizerOptions`

A debugger visualizer a plugin offers (M14b, ADR 0032): it appears in the View menu of the debugger's variable trees for values of `DebugVisualizerOptions.Types`. Since API 0.2.7.

| Member | Description |
|---|---|
| `DebugVisualizerOptions(string Id, string Title, IReadOnlyList<string> Types)` | A debugger visualizer a plugin offers (M14b, ADR 0032): it appears in the View menu of the debugger's variable trees for values of `DebugVisualizerOptions.Types`. Since API 0.2.7. |
| `string Id { get; init; }` | Unique within the plugin. |
| `string Title { get; init; }` | The menu item, e.g. "Dictionary Visualizer". |
| `IReadOnlyList<string> Types { get; init; }` | Type names as the debugger shows them (`System.Collections.Generic.Dictionary<string, int>`); a name ending in `*` matches as a prefix (`System.Collections.Generic.Dictionary<*`). |

### `DeviceCode`

`record Codalus.Plugins.DeviceCode`

A device code (RFC 8628) for the user to enter at `DeviceCode.VerificationUri`.

| Member | Description |
|---|---|
| `DeviceCode(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt)` | A device code (RFC 8628) for the user to enter at `DeviceCode.VerificationUri`. |
| `DateTimeOffset ExpiresAt { get; init; }` | When it stops working. |
| `string? Message { get; init; }` | The host's own instructions, if any. |
| `string UserCode { get; init; }` | The code, such as `WDJB-MJHT`. |
| `string VerificationUri { get; init; }` | Where to enter it. |
| `string? VerificationUriComplete { get; init; }` | A URL with the code filled in, when the host gives one. |

### `DirectoryEntry`

`record Codalus.Plugins.DirectoryEntry`

An entry of a directory.

| Member | Description |
|---|---|
| `DirectoryEntry(string Name, bool IsDirectory, long Size, DateTimeOffset Modified)` | An entry of a directory. |
| `bool IsDirectory { get; init; }` | Whether it's a directory. |
| `DateTimeOffset Modified { get; init; }` | When it last changed. |
| `string Name { get; init; }` | Its name (no separators). |
| `long Size { get; init; }` | A file's size in bytes. |

### `DocumentTestCandidates`

`record Codalus.Plugins.DocumentTestCandidates`

The test candidates of one document.

| Member | Description |
|---|---|
| `DocumentTestCandidates(string Uri, IReadOnlyList<TestCandidate> Candidates)` | The test candidates of one document. |
| `IReadOnlyList<TestCandidate> Candidates { get; init; }` | Classes first, each followed by its methods, in document order. |
| `string Uri { get; init; }` | The document. |

### `DocumentsChangedEventArgs`

`sealed class Codalus.Plugins.DocumentsChangedEventArgs` : EventArgs

Arguments of `IWorkspace.DocumentsChanged`.

| Member | Description |
|---|---|
| `DocumentsChangedEventArgs(IReadOnlyList<string> uris)` | Arguments of `IWorkspace.DocumentsChanged`. |
| `IReadOnlyList<string> Uris { get; }` | The changed documents. |

### `FileCoverage`

`record Codalus.Plugins.FileCoverage`

Coverage of one document.

| Member | Description |
|---|---|
| `FileCoverage(string Uri, IReadOnlyList<LineCoverage> Lines)` | Coverage of one document. |
| `IReadOnlyList<LineCoverage> Lines { get; init; }` | Its coverable lines. |
| `string Uri { get; init; }` | The document. |

### `FileStat`

`record Codalus.Plugins.FileStat`

What a provider knows about a path.

| Member | Description |
|---|---|
| `FileStat(bool Exists, bool IsDirectory, long Size, DateTimeOffset Modified)` | What a provider knows about a path. |
| `bool Exists { get; init; }` | Whether the path exists. |
| `bool IsDirectory { get; init; }` | Whether it's a directory. |
| `FileStat Missing { get; }` | A path that doesn't exist. |
| `DateTimeOffset Modified { get; init; }` | When it last changed. |
| `long Size { get; init; }` | A file's size in bytes. |

### `HostingAccount`

`record Codalus.Plugins.HostingAccount`

A signed-in account.

| Member | Description |
|---|---|
| `HostingAccount(string Name, string Host)` | A signed-in account. |
| `string? DisplayName { get; init; }` | The user's display name. |
| `string Host { get; init; }` | The host it is on, e.g. `github.com` or a GitHub Enterprise Server host. |
| `string Name { get; init; }` | The account name (login, user name or e-mail). |
| `string? Url { get; init; }` | The account's page. |

### `HostingAuthenticationException`

`sealed class Codalus.Plugins.HostingAuthenticationException` : Exception

The session isn't valid (expired, revoked, never signed in): the user must sign in again.

| Member | Description |
|---|---|
| `HostingAuthenticationException()` | Creates the exception. |
| `HostingAuthenticationException(string message)` | Creates the exception with a message for the user. |
| `HostingAuthenticationException(string message, Exception innerException)` | Creates the exception with a message and its cause. |

### `HostingOwner`

`record Codalus.Plugins.HostingOwner`

Where a repository can be created.

| Member | Description |
|---|---|
| `HostingOwner(string Id, string Name, string Kind)` | Where a repository can be created. |
| `string Id { get; init; }` | The provider's id for it (sent back in `NewRepository.Owner`). |
| `string Kind { get; init; }` | `user`, `organization`, `group` or `project`. |
| `string Name { get; init; }` | What the user sees. |

### `HostingProvider`

`abstract class Codalus.Plugins.HostingProvider`

A Git host. Override what the host supports: `HostingProvider.ResolveRepository` and the account members are required; the others throw `NotSupportedException` by default, and the shell hides what isn't supported. Calls come on any thread and are cancelled when the user moves on. Throw `HostingAuthenticationException` when the stored session is no longer valid (the shell then asks the user to sign in again).

| Member | Description |
|---|---|
| `string Id { get; }` | The id in the manifest, e.g. `github`. |
| `Task<PullRequest> CreatePullRequestAsync(HostingRepository repository, NewPullRequest request, CancellationToken cancellationToken)` | Opens a pull request. |
| `Task<HostingAccount?> GetAccountAsync(CancellationToken cancellationToken)` | The signed-in account from the stored session, or null when the user isn't signed in. No network call needed. |
| `Task<BranchStatus> GetBranchStatusAsync(HostingRepository repository, string branch, string? commit, CancellationToken cancellationToken)` | The CI status of a branch (its head commit when given). |
| `Task<PullRequestCheckout> GetCheckoutAsync(HostingRepository repository, PullRequest pullRequest, CancellationToken cancellationToken)` | What to fetch to check a pull request out, and the local branch to put it on. |
| `Task<IReadOnlyList<HostingOwner>> GetOwnersAsync(CancellationToken cancellationToken)` | Where the user can create repositories: their account, organizations, groups, or Azure DevOps projects. |
| `Task<PullRequestDetails> GetPullRequestAsync(HostingRepository repository, string number, CancellationToken cancellationToken)` | A pull request with its description and review threads. |
| `Task<IReadOnlyList<PullRequest>> GetPullRequestsAsync(HostingRepository repository, PullRequestFilter filter, CancellationToken cancellationToken)` | The repository's open pull requests (merge requests on GitLab), newest first. |
| `Task<HostingRepositoryInfo> GetRepositoryInfoAsync(HostingRepository repository, CancellationToken cancellationToken)` | The repository's default branch and web page. |
| `Task<IReadOnlyList<WorkItem>> GetWorkItemsAsync(HostingRepository repository, WorkItemQuery query, CancellationToken cancellationToken)` | Issues or work items: the ones assigned to the user, or those matching a number or words (commit message completion). |
| `Task<PublishedRepository> PublishAsync(NewRepository request, CancellationToken cancellationToken)` | Creates a repository (Publish): the shell then adds it as a remote and pushes. |
| `HostingRepository? ResolveRepository(string remoteUrl)` | The repository a remote URL (HTTPS or SSH) points to on this host, or null when it's another host's. |
| `Task<HostingAccount> SignInAsync(IDeviceCodePrompt prompt, CancellationToken cancellationToken)` | Signs in with the OAuth device flow: get a code from the host, show it with `prompt`, wait for the user to approve it in the browser, and store the session (`ISecrets`). Cancelled when the user cancels. |
| `Task<HostingAccount> SignInWithTokenAsync(string token, CancellationToken cancellationToken)` | Signs in with a personal access token the user pasted: check it, then store it. |
| `Task SignOutAsync(CancellationToken cancellationToken)` | Forgets the session (deletes the stored secrets). |

### `HostingRepository`

`record Codalus.Plugins.HostingRepository`

A repository on a host, as `HostingProvider.ResolveRepository` found it. `HostingRepository.Id` is the provider's own (`owner/name`, `organization/project/repository`, a GitLab path).

| Member | Description |
|---|---|
| `HostingRepository(string Id, string Owner, string Name, string WebUrl)` | A repository on a host, as `HostingProvider.ResolveRepository` found it. `HostingRepository.Id` is the provider's own (`owner/name`, `organization/project/repository`, a GitLab path). |
| `string Host { get; init; }` | The host, e.g. `github.com`. |
| `string Id { get; init; }` | The provider's identifier for the repository. |
| `string Name { get; init; }` | The repository's name. |
| `string Owner { get; init; }` | The owner, organization, group or project. |
| `string? Remote { get; init; }` | The git remote it was found on (set by the shell), e.g. `origin`. |
| `string WebUrl { get; init; }` | Its page. |

### `HostingRepositoryInfo`

`record Codalus.Plugins.HostingRepositoryInfo`

A repository's default branch and page.

| Member | Description |
|---|---|
| `HostingRepositoryInfo(string DefaultBranch, string WebUrl)` | A repository's default branch and page. |
| `string DefaultBranch { get; init; }` | The default branch, e.g. `main`. |
| `bool IsPrivate { get; init; }` | Whether it is private. |
| `string WebUrl { get; init; }` | The repository's page. |

### `IAuthentication`

`interface Codalus.Plugins.IAuthentication`

Authentication providers (API 1.0, M22a): offering one, and asking any for a session.

| Member | Description |
|---|---|
| `Task<AuthenticationSession?> GetSessionAsync(string providerId, IReadOnlyList<string> scopes, bool createIfNone, CancellationToken cancellationToken)` | A session of provider `providerId` with `scopes`: an existing one, or, with `createIfNone`, a new sign-in. Another plugin's provider asks the user's consent first. Null when there's none, or the user declined. |
| `void NotifySessionsChanged(string providerId)` | The provider's sessions changed (a sign-in, a sign-out): the Accounts page refreshes. |
| `IDisposable RegisterProvider(string id, string label, IAuthenticationProvider provider)` | Offers `provider` as `id` (the manifest's `contributes.authentication`) until disposed. The Accounts page lists its sessions. |

### `IAuthenticationProvider`

`interface Codalus.Plugins.IAuthenticationProvider`

Signs users in to a service (API 1.0, M22a), for its own plugin and any other that asks (`IAuthentication.GetSessionAsync`). Keep tokens in `IPluginContext.Secrets`.

| Member | Description |
|---|---|
| `Task<AuthenticationSession> CreateSessionAsync(IReadOnlyList<string> scopes, CancellationToken cancellationToken)` | Signs the user in (a browser, a device code, a prompt) and returns the new session. |
| `Task<IReadOnlyList<AuthenticationSession>> GetSessionsAsync(IReadOnlyList<string>? scopes, CancellationToken cancellationToken)` | The sessions there are, those with every scope of `scopes` when given. |
| `Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken)` | Signs the session out and forgets its token. |

### `IBuild`

`interface Codalus.Plugins.IBuild`

Builds through the shell.

| Member | Description |
|---|---|
| `event EventHandler<BuildFinishedEventArgs>? Finished` | Any build finished (also the user's). |
| `Task<BuildResult> BuildAsync(IReadOnlyList<string>? projects, CancellationToken cancellationToken)` | Builds `projects` (and what they reference), or the solution when null. Waits for a build in progress to finish first. Output goes to the Build channel and the Error List. |

### `ICodalusPlugin`

`interface Codalus.Plugins.ICodalusPlugin`

The entry point of a plugin. The plugin host creates one instance (it needs a public parameterless constructor) and calls `ICodalusPlugin.ActivateAsync` when one of the plugin's activation events fires.

| Member | Description |
|---|---|
| `Task ActivateAsync(IPluginContext context, CancellationToken cancellationToken)` | Registers commands, views and status items. Keep it fast: it runs on the user's critical path. |
| `Task DeactivateAsync(CancellationToken cancellationToken)` | Called on shutdown or when the plugin is disabled. `IPluginContext.Subscriptions` are disposed afterwards. |

### `ICommands`

`interface Codalus.Plugins.ICommands`

The command system, shared by the shell and all plugins.

| Member | Description |
|---|---|
| `Task<JsonElement?> ExecuteAsync(string commandId, CancellationToken cancellationToken, params object?[] args)` | Executes any command (built-in or contributed) and returns its JSON result. |
| `IDisposable Register(string commandId, Action handler)` | Provides a command implementation without arguments or result. |
| `IDisposable Register(string commandId, Func<CommandInvocation, CancellationToken, Task<object?>> handler)` | Provides the implementation of a command. Commands declared in the manifest appear in menus and the command palette before activation; registering an undeclared id creates a hidden command that other code can execute. The handler's result (any JSON-serializable value) is returned to the caller. |

### `ICustomEditorProvider`

`interface Codalus.Plugins.ICustomEditorProvider`

A custom editor (API 1.0, M22a): files matching the manifest's `customEditors` open in a document whose content is a declarative view the plugin sets with `IWindow.SetView`. To change the file, the plugin gives its new text with `IWindow.SetCustomEditorText`; the user saves it.

| Member | Description |
|---|---|
| `Task CloseAsync(string viewId, CancellationToken cancellationToken)` | Its document closed. |
| `Task OpenAsync(CustomEditorDocument document, CancellationToken cancellationToken)` | A file opened: fill its view (`CustomEditorDocument.ViewId`) and handle its events. |

### `IDebug`

`interface Codalus.Plugins.IDebug`

Debug sessions (netcoredbg, ADR 0007). One session at a time.

| Member | Description |
|---|---|
| `Task<IDebugSession> AttachAsync(int processId, CancellationToken cancellationToken)` | Attaches to a running .NET process. |
| `Task<IDebugSession> LaunchAsync(DebugLaunchOptions options, CancellationToken cancellationToken)` | Starts a program under the debugger; completes once its process id is known. |
| `IDisposable RegisterVisualizer(DebugVisualizerOptions options, Func<IDebugVisualizerContext, CancellationToken, Task<VisualizerView>> visualize)` | Offers a visualizer for values of the given types (since API 0.2.7). `visualize` runs when the user picks it, while the program is paused. Dispose the result to withdraw it. Activate with `onDebug`. |

### `IDebugSession`

`interface Codalus.Plugins.IDebugSession`

A debug session started by a plugin.

| Member | Description |
|---|---|
| `Task<int?> Ended { get; }` | Completes when the session ends, with the exit code when the program exited. |
| `int ProcessId { get; }` | The debuggee's process id. |
| `Task StopAsync(CancellationToken cancellationToken = default)` | Ends the session (terminates a launched program, detaches from an attached one). |

### `IDebugVisualizerContext`

`interface Codalus.Plugins.IDebugVisualizerContext`

The value being visualized, and evaluation in the paused program's selected frame.

| Member | Description |
|---|---|
| `string Expression { get; }` | The expression that evaluates to the value (use it to build further expressions). |
| `string? Type { get; }` | The value's type as the debugger shows it. |
| `string Value { get; }` | The value as the debugger shows it. |
| `Task<DebugValue> EvaluateAsync(string expression, CancellationToken cancellationToken)` | Evaluates an expression in the paused program (netcoredbg: members, indexers and method calls; no `new`, casts or lambdas). Throws `InvalidOperationException` with the debugger's message when it fails. |

### `IDeviceCodePrompt`

`interface Codalus.Plugins.IDeviceCodePrompt`

Shows a device code to the user (the shell's sign-in dialog).

| Member | Description |
|---|---|
| `Task ShowAsync(DeviceCode code, CancellationToken cancellationToken)` | Shows the code and where to enter it. Returns once it is shown; the sign-in call's token cancels it. |

### `IFileSystemProvider`

`interface Codalus.Plugins.IFileSystemProvider`

Files a plugin serves under a scheme (API 1.0, M22a): an archive's contents, a cloud store, a database's scripts. The IDE shows them under a root of their own (`IFileSystems.RootOf`) and its editor reads and saves through the provider. Paths are the provider's own: `/`-separated, starting with `/` (`/` is the root). A read-only provider leaves the write members as they are.

| Member | Description |
|---|---|
| `Task CreateDirectoryAsync(string path, CancellationToken cancellationToken)` | Creates the directory (and its parents). |
| `Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken)` | Deletes the file or directory (a non-empty directory only when `recursive`). |
| `Task<IReadOnlyList<DirectoryEntry>> ReadDirectoryAsync(string path, CancellationToken cancellationToken)` | The entries of the directory `path`. |
| `Task<byte[]> ReadFileAsync(string path, CancellationToken cancellationToken)` | The file's content. |
| `Task RenameAsync(string source, string target, bool overwrite, CancellationToken cancellationToken)` | Moves or renames. |
| `Task<FileStat> StatAsync(string path, CancellationToken cancellationToken)` | What `path` is; `FileStat.Missing` when it doesn't exist. |
| `Task WriteFileAsync(string path, byte[] content, CancellationToken cancellationToken)` | Creates or replaces the file. |

### `IFileSystems`

`interface Codalus.Plugins.IFileSystems`

File systems plugins provide (API 1.0, M22a).

| Member | Description |
|---|---|
| `void NotifyChanged(string scheme, IReadOnlyList<string> paths, string kind = "changed")` | Tells the IDE files changed (`created`, `changed` or `deleted`), so open editors reload. |
| `IDisposable RegisterProvider(string scheme, IFileSystemProvider provider)` | Serves `scheme` (letters, digits, `-` and `.`) from `provider` until disposed. |
| `string RootOf(string scheme)` | Where the IDE shows `scheme`'s files: a path on this machine's form, under which `/a/b.txt` is `a/b.txt`. |

### `IHosting`

`interface Codalus.Plugins.IHosting`

Git hosting providers (ADR 0031): GitHub, Azure DevOps, GitLab, or another host. The shell shows the pull requests, the sign-in dialog, review comments, CI status and work items; the provider talks to its host. A provider is declared in the manifest (`contributes.hostingProviders`) and registered when its plugin activates (`onHostingProvider:<id>`, implicit). Since API 0.2.6.

| Member | Description |
|---|---|
| `IDisposable RegisterProvider(HostingProvider provider)` | Registers the provider whose `HostingProvider.Id` the manifest declares. Dispose to unregister. |

### `IOutputChannel`

`interface Codalus.Plugins.IOutputChannel`

A text channel in the Output window.

| Member | Description |
|---|---|
| `string Name { get; }` | The channel's display name. |
| `void Append(string text)` | Appends text as-is. |
| `void AppendLine(string line)` | Appends text followed by a newline. |
| `void Clear()` | Removes all text. |
| `void Show()` | Reveals the channel in the Output window. |

### `IPluginContext`

`interface Codalus.Plugins.IPluginContext`

Everything a plugin can use. Obtained in `ICodalusPlugin.ActivateAsync`.

| Member | Description |
|---|---|
| `IAuthentication Authentication { get; }` | Authentication providers: offering one, and asking any for a session (since 1.0). |
| `IBuild Build { get; }` | Builds through the IDE's build (one at a time, with the Build output). |
| `ICommands Commands { get; }` | Register and execute commands. |
| `IDebug Debug { get; }` | Debug sessions through the IDE's debugger, and debugger visualizers. |
| `IFileSystems FileSystems { get; }` | File systems the plugin provides (since 1.0). |
| `IHosting Hosting { get; }` | Git hosting providers: pull requests, review comments, CI status, work items, publishing (ADR 0031). |
| `IPluginLog Log { get; }` | The plugin's log, shown in its Output channel. |
| `PluginInfo Plugin { get; }` | The running plugin's identity. |
| `ISecrets Secrets { get; }` | The plugin's secrets, in the OS credential store. |
| `ISettings Settings { get; }` | Effective settings and change notifications. |
| `ISourceControl SourceControl { get; }` | Source control providers (since 1.0). |
| `IList<IDisposable> Subscriptions { get; }` | Disposables owned by the plugin; disposed when it is deactivated. |
| `ITerminals Terminals { get; }` | Integrated terminals: open a shell or run a command in one, send it text, read its output. |
| `ITests Tests { get; }` | Test controllers for the Test Explorer (ADR 0010). |
| `IWindow Window { get; }` | Notifications, Output channels, status bar items and tool window content. |
| `IWorkspace Workspace { get; }` | The open solution, its projects, packages and outputs, and test candidates from the language service. |

### `IPluginLog`

`interface Codalus.Plugins.IPluginLog`

Writes to the plugin's Output channel; warnings and errors are also logged by the shell.

| Member | Description |
|---|---|
| `void Write(PluginLogLevel level, string message)` | Writes one message. |

### `ISecrets`

`interface Codalus.Plugins.ISecrets`

The plugin's own secrets (tokens), kept in the OS credential store: Windows Credential Manager, the macOS Keychain or the Secret Service on Linux. Never in a file. A key is the plugin's own; other plugins can't read it. Since API 0.2.6.

| Member | Description |
|---|---|
| `Task DeleteAsync(string key, CancellationToken cancellationToken)` | Deletes a secret (no error when there is none). |
| `Task<string?> GetAsync(string key, CancellationToken cancellationToken)` | The secret, or null when none is stored under `key`. |
| `Task StoreAsync(string key, string value, CancellationToken cancellationToken)` | Stores or replaces a secret. |

### `ISettings`

`interface Codalus.Plugins.ISettings`

Read access to effective settings (including the plugin's own contributed settings).

| Member | Description |
|---|---|
| `event EventHandler<SettingsChangedEventArgs>? Changed` | Raised when effective values change (user, workspace or default). |
| `T GetValue<T>(string key, T fallback)` | The setting's value, or `fallback` when unset or not convertible. |

### `ISourceControl`

`interface Codalus.Plugins.ISourceControl`

Source control providers (API 1.0, M22a).

| Member | Description |
|---|---|
| `IDisposable RegisterProvider(string id, string label, string root, ISourceControlProvider provider)` | Provides source control as `id` for the folder `root` until disposed. |
| `void Update(string id, SourceControlState state)` | What the provider shows now. |

### `ISourceControlProvider`

`interface Codalus.Plugins.ISourceControlProvider`

A source control system a plugin provides (API 1.0, M22a): Mercurial, Perforce, Subversion. The Source Control Providers window shows its state (`ISourceControl.Update`); its commit box calls `ISourceControlProvider.AcceptAsync`, and opening a file compares it with `ISourceControlProvider.GetOriginalAsync`. Git is built in.

| Member | Description |
|---|---|
| `Task AcceptAsync(string message, CancellationToken cancellationToken)` | Commits (submits, checks in) with the commit box's message. |
| `Task<string?> GetOriginalAsync(string path, CancellationToken cancellationToken)` | The file as the repository has it (to compare with); null when it's new. |

### `IStatusBarItem`

`interface Codalus.Plugins.IStatusBarItem` : IDisposable

An entry in the status bar. Property changes are pushed to the shell while it is shown.

| Member | Description |
|---|---|
| `string? Command { get; set; }` | Command executed when the item is clicked. |
| `string Text { get; set; }` | The visible text. |
| `string? Tooltip { get; set; }` | Hover text. |
| `void Hide()` | Hides the item (it can be shown again). |
| `void Show()` | Makes the item visible. |

### `ITerminal`

`interface Codalus.Plugins.ITerminal` : IAsyncDisposable

A terminal a plugin opened.

| Member | Description |
|---|---|
| `Task<int?> Exited { get; }` | The program's exit code once it exited (null when it couldn't be determined). |
| `string Id { get; }` | The shell's id of the terminal. |
| `int ProcessId { get; }` | The process running in the terminal. |
| `event EventHandler<TerminalOutputEventArgs>? Output` | Text the program wrote, escape sequences included, as it arrives (with `TerminalOptions.ReportOutput`). |
| `Task KillAsync(CancellationToken cancellationToken)` | Ends the program and closes the terminal. Disposing does the same. |
| `Task SendTextAsync(string text, CancellationToken cancellationToken)` | Sends text as if typed. Add `"\r"` to run a command line. |

### `ITerminals`

`interface Codalus.Plugins.ITerminals`

The integrated terminals (ADR 0014). Since API 0.2.3.

| Member | Description |
|---|---|
| `Task<ITerminal> CreateAsync(TerminalOptions options, CancellationToken cancellationToken)` | Opens a terminal: the user's shell (`TerminalOptions.Profile`), or `TerminalOptions.Program` run in a terminal ("run command in terminal"), which ends when the program exits. |

### `ITestController`

`interface Codalus.Plugins.ITestController` : IDisposable

Owns a tree of `TestItem`s and the ways to run them. Changes to items are sent to the shell in batches. Item ids must be unique within the controller.

| Member | Description |
|---|---|
| `string Id { get; }` | The id given to `ITests.CreateController`. |
| `TestItemCollection Items { get; }` | The root items (typically one per project). |
| `string Label { get; }` | The name shown in the Test Explorer. |
| `Func<CancellationToken, Task>? RefreshHandler { get; set; }` | Called when the user asks to reload the tests (the Test Explorer's Refresh). The returned task ends the refresh indicator. |
| `ITestRun CreateRun(TestRunRequest request, string? name = null)` | Starts reporting a run. Call `ITestRun.Complete` when it is over. |
| `ITestRunProfile CreateRunProfile(string label, TestRunProfileKind kind, Func<TestRunRequest, CancellationToken, Task> handler, bool isDefault = true)` | A way to run tests: Run, Debug or Coverage. The shell calls `handler` with the user's selection; cancelling its token means the user pressed Cancel. The default profile of each kind is used by toolbar and editor commands. |
| `TestItem? Find(string id)` | The item with `id` anywhere in the tree, or null. |

### `ITestRun`

`interface Codalus.Plugins.ITestRun`

Reports the progress and results of one run. Methods may be called from any thread.

| Member | Description |
|---|---|
| `string Name { get; }` | The run's name, shown in the results. |
| `void AddAttachment(TestAttachment attachment)` | A file the run produced (a TRX file, a coverage report…). |
| `void AddCoverage(CoverageReport coverage)` | Coverage collected by the run (ADR 0011). Several calls are merged. |
| `void AppendOutput(string text, TestItem? item = null)` | Output of the run; with `item`, output of that test. |
| `void Complete()` | The run is over; tests still enqueued or started are shown as not run. |
| `void Enqueued(TestItem item)` | The test is waiting to run. |
| `void Errored(TestItem item, IReadOnlyList<TestMessage> messages, TimeSpan? duration = null)` | The test could not run, or threw something other than an assertion failure. |
| `void Failed(TestItem item, IReadOnlyList<TestMessage> messages, TimeSpan? duration = null)` | An assertion failed. |
| `void Passed(TestItem item, TimeSpan? duration = null)` | The test passed. |
| `void Skipped(TestItem item, string? reason = null)` | The test was skipped, optionally with the framework's reason. |
| `void Started(TestItem item)` | The test is running. |

### `ITestRunProfile`

`interface Codalus.Plugins.ITestRunProfile` : IDisposable

A way to run tests, shown in the Test Explorer and the editor. Dispose to remove it.

| Member | Description |
|---|---|
| `bool IsDefault { get; }` | Whether toolbar and editor commands of this kind use it. |
| `TestRunProfileKind Kind { get; }` | Run, Debug or Coverage. |
| `string Label { get; }` | The name shown in menus. |

### `ITests`

`interface Codalus.Plugins.ITests`

Creates test controllers (ADR 0010), modelled on VS Code's testing API.

| Member | Description |
|---|---|
| `ITestController CreateController(string id, string label)` | A controller whose items appear in the Test Explorer. `id` is unique per plugin. |

### `ITreeDataProvider<T>`

`interface Codalus.Plugins.ITreeDataProvider<T>`

Provides the elements of a tree view lazily, one level at a time.

| Member | Description |
|---|---|
| `event EventHandler<TreeDataChangedEventArgs<T>>? Changed` | Raise to make the shell re-read (part of) the tree. |
| `Task<IReadOnlyList<T>> GetChildrenAsync(T element, CancellationToken cancellationToken)` | Children of `element`, or the root elements when it is null/default. |
| `TreeItem GetTreeItem(T element)` | How `element` is displayed. |

### `IWindow`

`interface Codalus.Plugins.IWindow`

The parts of the workbench UI a plugin can contribute to.

| Member | Description |
|---|---|
| `IOutputChannel CreateOutputChannel(string name)` | A channel of the Output window, shown as "&lt;plugin&gt;: &lt;name&gt;". |
| `IStatusBarItem CreateStatusBarItem(string id, StatusBarAlignment alignment = StatusBarAlignment.Left, int priority = 0)` | A status bar entry. It stays hidden until `IStatusBarItem.Show`. |
| `IDisposable OnViewEvent(string viewId, Func<ViewEvent, CancellationToken, Task> handler)` | What the user does in a declarative view (since 1.0). |
| `IDisposable RegisterCustomEditor(string viewType, ICustomEditorProvider provider)` | Provides the custom editor `viewType` of the manifest's `customEditors` (since 1.0). |
| `IDisposable RegisterTreeDataProvider<T>(string viewId, ITreeDataProvider<T> provider)` | Supplies the content of a tool window declared with `"kind": "tree"`. |
| `void SetCustomEditorText(string viewId, string text)` | A custom editor's file has new text: its document shows as modified, and Save writes it (since 1.0). |
| `void SetMarkdown(string viewId, string markdown)` | Sets the content of a tool window declared with `"kind": "markdown"`. |
| `void SetView(string viewId, ViewNode root)` | Sets a declarative view's content (since 1.0): a tool window of kind `view`, or a custom editor's document. Setting it again replaces it; the user's input in elements that stay (by `ViewNode.Id`) is kept. |
| `Task<string?> ShowInputBoxAsync(InputBoxOptions options, CancellationToken cancellationToken = default)` | Asks the user for a line of text. Completes with the text, or null when cancelled. Since API 0.1.1. |
| `Task<string?> ShowNotificationAsync(NotificationSeverity severity, string message, IReadOnlyList<string>? actions = null, CancellationToken cancellationToken = default)` | Shows a notification. Completes with the chosen action, or null when the user dismissed it. |
| `Task<QuickPickItem?> ShowQuickPickAsync(IReadOnlyList<QuickPickItem> items, string? placeholder = null, CancellationToken cancellationToken = default)` | Lets the user pick one of `items` in the command palette (fuzzy filtering, keyboard). Completes with the chosen item, or null when cancelled. Since API 0.1.1. |
| `Task<T> WithProgressAsync<T>(string title, Func<IProgress<ProgressReport>, CancellationToken, Task<T>> work, bool cancellable = false, CancellationToken cancellationToken = default)` | Runs `work` as a task the user sees in the status bar, with what it reports (since 1.0). When `cancellable`, the user can cancel it: the token passed to `work` is cancelled. |

### `IWorkspace`

`interface Codalus.Plugins.IWorkspace`

The open solution as the build host evaluated it, plus test discovery from source.

| Member | Description |
|---|---|
| `event EventHandler<DocumentsChangedEventArgs>? DocumentsChanged` | Documents were edited or saved (debounced), as `file:` URIs. |
| `event EventHandler? SolutionChanged` | A solution was opened or closed, or a project was (re)evaluated. Call `IWorkspace.GetSolutionAsync`. |
| `Task<IReadOnlyList<DocumentTestCandidates>> FindTestCandidatesAsync(IReadOnlyList<string> documentUris, CancellationToken cancellationToken)` | Test methods and classes found in the documents' syntax (open documents use their unsaved text). Works before design-time builds and builds. |
| `Task<IReadOnlyList<WorkspacePackage>> GetPackagesAsync(string projectPath, CancellationToken cancellationToken)` | The NuGet packages of a project: top-level references (with the requested and restored versions) and transitive packages from its last restore. Read only. Since API 0.2.2. |
| `Task<ProjectOutput> GetProjectOutputAsync(string projectPath, string? targetFramework, CancellationToken cancellationToken)` | The project's build output for `targetFramework` (default: the active framework). |
| `Task<IReadOnlyDictionary<string, string>> GetProjectPropertiesAsync(string projectPath, IReadOnlyList<string> names, CancellationToken cancellationToken)` | Evaluated MSBuild properties of a project (with the active configuration); unset properties are empty. Read only. Since API 0.2.2. |
| `Task<WorkspaceSolution?> GetSolutionAsync(CancellationToken cancellationToken)` | The open solution, or null when none is open. |
| `Task<TypeOutline?> GetTypeOutlineAsync(string documentUri, int line, int character, CancellationToken cancellationToken)` | The class, record or struct at a zero-based position of a document, with its methods; null when there is none. |

### `InputBoxOptions`

`record Codalus.Plugins.InputBoxOptions`

What `IWindow.ShowInputBoxAsync` asks. Since API 0.1.1.

| Member | Description |
|---|---|
| `InputBoxOptions(string Title)` | What `IWindow.ShowInputBoxAsync` asks. Since API 0.1.1. |
| `string? Prompt { get; init; }` | Explanatory text under the title. |
| `string Title { get; init; }` | The question, for example "Project name". |
| `string? Value { get; init; }` | The initial text. |

### `LineCoverage`

`record Codalus.Plugins.LineCoverage`

Coverage of one line (zero-based). `LineCoverage.BranchesTotal` is 0 for lines without branches.

| Member | Description |
|---|---|
| `LineCoverage(int Line, int Hits, int BranchesCovered = 0, int BranchesTotal = 0)` | Coverage of one line (zero-based). `LineCoverage.BranchesTotal` is 0 for lines without branches. |
| `int BranchesCovered { get; init; }` | Its branches that ran. |
| `int BranchesTotal { get; init; }` | Its branches; 0 when it has none. |
| `int Hits { get; init; }` | How many times it ran. |
| `int Line { get; init; }` | The line (zero-based). |

### `MethodCoverage`

`record Codalus.Plugins.MethodCoverage`

Coverage of one method, for the Coverage Results tree (assembly, namespace, class, method).

| Member | Description |
|---|---|
| `MethodCoverage(string Assembly, string? Namespace, string Class, string Method, string? Uri, int Line, int LinesCovered, int LinesTotal, int BranchesCovered, int BranchesTotal)` | Coverage of one method, for the Coverage Results tree (assembly, namespace, class, method). |
| `string Assembly { get; init; }` | The assembly the method is in. |
| `int BranchesCovered { get; init; }` | Its branches that ran. |
| `int BranchesTotal { get; init; }` | Its branches. |
| `string Class { get; init; }` | Its class. |
| `int Line { get; init; }` | Its first line (zero-based). |
| `int LinesCovered { get; init; }` | Its lines that ran. |
| `int LinesTotal { get; init; }` | Its coverable lines. |
| `string Method { get; init; }` | Its name. |
| `string? Namespace { get; init; }` | Its namespace (null for the global namespace). |
| `string? Uri { get; init; }` | The document it is in, when known. |

### `MethodOutline`

`record Codalus.Plugins.MethodOutline`

A public or internal method of a `TypeOutline`.

| Member | Description |
|---|---|
| `MethodOutline(string Name, string ReturnType, IReadOnlyList<ParameterOutline> Parameters, bool IsStatic, bool IsAsync, int Line)` | A public or internal method of a `TypeOutline`. |
| `bool IsAsync { get; init; }` | Whether it is async or returns a task. |
| `bool IsStatic { get; init; }` | Whether it is static. |
| `int Line { get; init; }` | The zero-based line of its name. |
| `string Name { get; init; }` | The method's name. |
| `IReadOnlyList<ParameterOutline> Parameters { get; init; }` | Its parameters. |
| `string ReturnType { get; init; }` | Its return type as written. |

### `NewPullRequest`

`record Codalus.Plugins.NewPullRequest`

A pull request to open.

| Member | Description |
|---|---|
| `NewPullRequest(string Title, string SourceBranch, string TargetBranch)` | A pull request to open. |
| `string Description { get; init; }` | The description (Markdown). |
| `bool IsDraft { get; init; }` | Open it as a draft. |
| `string SourceBranch { get; init; }` | The branch with the changes (already pushed). |
| `string TargetBranch { get; init; }` | The branch to merge into. |
| `string Title { get; init; }` | Its title. |

### `NewRepository`

`record Codalus.Plugins.NewRepository`

A repository to create.

| Member | Description |
|---|---|
| `NewRepository(string Owner, string Name)` | A repository to create. |
| `string? Description { get; init; }` | A description. |
| `bool IsPrivate { get; init; }` | Private (the default) or public. |
| `string Name { get; init; }` | The repository's name. |
| `string Owner { get; init; }` | A `HostingOwner.Id`. |

### `NotificationSeverity`

`enum Codalus.Plugins.NotificationSeverity`

Severity of a notification.

| Member | Description |
|---|---|
| `Error = 2` | Something failed. |
| `Info = 0` | Informational; dismissed automatically after a few seconds. |
| `Warning = 1` | Something needs the user's attention. |

### `ParameterOutline`

`record Codalus.Plugins.ParameterOutline`

A method's parameter, its type as written in source.

| Member | Description |
|---|---|
| `ParameterOutline(string Name, string Type)` | A method's parameter, its type as written in source. |
| `string Name { get; init; }` | The parameter's name. |
| `string Type { get; init; }` | Its type as written. |

### `PluginApi`

`static class Codalus.Plugins.PluginApi`

Constants of this version of the plugin API.

| Member | Description |
|---|---|
| `const string DeprecationDiagnostic = "CODALUS0001"` | The diagnostic id of deprecated API members (`[Obsolete(..., DiagnosticId = "CODALUS0001")]`). |
| `const string Version = "1.0.0"` | The API version that a plugin's `engines.codalus` range is checked against. Since 1.0 (M22a), minor versions only add to the API and a member goes away only after a major version, deprecated first with the `PluginApi.DeprecationDiagnostic` warning (ADR 0003, docs/plugins.md#changelog). |

### `PluginInfo`

`record Codalus.Plugins.PluginInfo`

Identity of the running plugin.

| Member | Description |
|---|---|
| `PluginInfo(string Id, string Version, string InstallPath)` | Identity of the running plugin. |
| `string Id { get; init; }` | The manifest id, `publisher.name`. |
| `string InstallPath { get; init; }` | The folder containing `codalus-plugin.json`. |
| `string Version { get; init; }` | The plugin's SemVer version. |

### `PluginLocalization`

`static class Codalus.Plugins.PluginLocalization`

A plugin's own text in the display language (since API 0.2.9, M23c). The strings come from the same files as the manifest's (`codalus-plugin.nls.<locale>.json`, then the language's, next to the manifest), and a key no file has shows `english`. The display language is `PluginLocalization.LocaleVariable`, which Codalus IDE sets for the processes it starts.

| Member | Description |
|---|---|
| `const string LocaleVariable = "CODALUS_DISPLAY_LOCALE"` | The display language's environment variable (a BCP 47 name, or a pseudo-language). |
| `static string Localize(IPluginContext context, string key, string english)` | The text for `key` in the display language, else `english`. |
| `static string Localize(IPluginContext context, string key, string english, params object?[] args)` | `PluginLocalization.Localize` for a pattern with `{0}` placeholders. |

### `PluginLogExtensions`

`static class Codalus.Plugins.PluginLogExtensions`

Convenience methods for `IPluginLog`.

| Member | Description |
|---|---|
| `static void Debug(IPluginLog log, string message)` | Writes a `PluginLogLevel.Debug` message. |
| `static void Error(IPluginLog log, string message)` | Writes a `PluginLogLevel.Error` message. |
| `static void Info(IPluginLog log, string message)` | Writes a `PluginLogLevel.Info` message. |
| `static void Warning(IPluginLog log, string message)` | Writes a `PluginLogLevel.Warning` message. |

### `PluginLogLevel`

`enum Codalus.Plugins.PluginLogLevel`

Severity of a log message.

| Member | Description |
|---|---|
| `Debug = 0` | Diagnostic detail. |
| `Error = 3` | A failure. |
| `Info = 1` | Normal operation. |
| `Warning = 2` | Something unexpected that the plugin recovered from. |

### `ProgressReport`

`record Codalus.Plugins.ProgressReport`

What a task reports: a message, and how far it is (0 to 100). A null keeps the previous value.

| Member | Description |
|---|---|
| `ProgressReport(string? Message = null, double? Percent = null)` | What a task reports: a message, and how far it is (0 to 100). A null keeps the previous value. |
| `string? Message { get; init; }` | What the task is doing now; null keeps the previous message. |
| `double? Percent { get; init; }` | How far it is, 0 to 100; null keeps the previous value (or an indeterminate bar). |

### `ProjectOutput`

`record Codalus.Plugins.ProjectOutput`

What a build of the project produces and how `dotnet run` would start it.

| Member | Description |
|---|---|
| `ProjectOutput(string Path, string? TargetFramework, string? TargetPath, string? RunCommand, string? RunArguments, string? RunWorkingDirectory)` | What a build of the project produces and how `dotnet run` would start it. |
| `string Path { get; init; }` | The project file. |
| `string? RunArguments { get; init; }` | Its arguments. |
| `string? RunCommand { get; init; }` | The program `dotnet run` starts. |
| `string? RunWorkingDirectory { get; init; }` | Its working directory. |
| `string? TargetFramework { get; init; }` | The target framework built. |
| `string? TargetPath { get; init; }` | The assembly the build produces. |

### `PublishedRepository`

`record Codalus.Plugins.PublishedRepository`

A repository that was created.

| Member | Description |
|---|---|
| `PublishedRepository(string CloneUrl, string WebUrl)` | A repository that was created. |
| `string CloneUrl { get; init; }` | Its HTTPS clone URL. |
| `string? SshUrl { get; init; }` | Its SSH clone URL. |
| `string WebUrl { get; init; }` | Its page. |

### `PullRequest`

`record Codalus.Plugins.PullRequest`

A pull request (a merge request on GitLab).

| Member | Description |
|---|---|
| `PullRequest(string Number, string Title, string Author, string SourceBranch, string TargetBranch, string WebUrl)` | A pull request (a merge request on GitLab). |
| `string Author { get; init; }` | Who opened it. |
| `DateTimeOffset? CreatedAt { get; init; }` | When it was opened. |
| `bool IsDraft { get; init; }` | Whether it is a draft. |
| `string Number { get; init; }` | Its number on the host (as text). |
| `string Reference { get; init; }` | How it is referred to: `#12`, or `!12` on GitLab. |
| `IReadOnlyList<string> Reviewers { get; init; }` | Reviewers asked (names). |
| `string SourceBranch { get; init; }` | The branch with the changes. |
| `string? SourceCloneUrl { get; init; }` | The fork's clone URL, when it comes from one. |
| `string? SourceCommit { get; init; }` | The head commit. |
| `string? SourceRepository { get; init; }` | The fork it comes from (`owner/name`), or null when its branch is in the repository itself. |
| `string State { get; init; }` | `open`, `merged` or `closed`. |
| `string TargetBranch { get; init; }` | The branch it merges into. |
| `string Title { get; init; }` | Its title. |
| `DateTimeOffset? UpdatedAt { get; init; }` | When it last changed. |
| `string WebUrl { get; init; }` | Its page. |

### `PullRequestCheckout`

`record Codalus.Plugins.PullRequestCheckout`

How to check out a pull request: fetch `PullRequestCheckout.SourceRef` from `PullRequestCheckout.Remote` (a remote's name or a fork's URL) onto `PullRequestCheckout.LocalBranch`. `PullRequestCheckout.Track`: `PullRequestCheckout.SourceRef` is a branch of that remote, and the local branch tracks it.

| Member | Description |
|---|---|
| `PullRequestCheckout(string Remote, string SourceRef, string LocalBranch, bool Track)` | How to check out a pull request: fetch `PullRequestCheckout.SourceRef` from `PullRequestCheckout.Remote` (a remote's name or a fork's URL) onto `PullRequestCheckout.LocalBranch`. `PullRequestCheckout.Track`: `PullRequestCheckout.SourceRef` is a branch of that remote, and the local branch tracks it. |
| `string LocalBranch { get; init; }` | The local branch to create or update. |
| `string Remote { get; init; }` | A remote name or URL. |
| `string SourceRef { get; init; }` | A branch name, or a ref such as `refs/pull/12/head`. |
| `bool Track { get; init; }` | Whether the local branch tracks the remote branch. |

### `PullRequestDetails`

`record Codalus.Plugins.PullRequestDetails`

A pull request with its description and review threads.

| Member | Description |
|---|---|
| `PullRequestDetails(PullRequest PullRequest, string Description, IReadOnlyList<ReviewThread> Threads)` | A pull request with its description and review threads. |
| `IReadOnlyList<ReviewComment> Comments { get; init; }` | General comments (not on a line), oldest first. |
| `string Description { get; init; }` | Its description (Markdown). |
| `PullRequest PullRequest { get; init; }` | The pull request. |
| `IReadOnlyList<ReviewThread> Threads { get; init; }` | Its review threads on lines of files. |

### `PullRequestFilter`

`enum Codalus.Plugins.PullRequestFilter`

Which pull requests to list.

| Member | Description |
|---|---|
| `All = 0` | Every open pull request. |
| `Mine = 1` | The user's own. |
| `ReviewRequested = 2` | Those that ask for the user's review. |

### `QuickPickItem`

`record Codalus.Plugins.QuickPickItem`

An entry of `IWindow.ShowQuickPickAsync`. Since API 0.1.1.

| Member | Description |
|---|---|
| `QuickPickItem(string Label)` | An entry of `IWindow.ShowQuickPickAsync`. Since API 0.1.1. |
| `string? Description { get; init; }` | Muted text shown after the label. |
| `string? Detail { get; init; }` | Muted text shown on the right (for example a key or a path). |
| `string Label { get; init; }` | The text that is filtered and shown. |
| `object? Tag { get; init; }` | Your own data; not sent to the shell. |

### `ReviewComment`

`record Codalus.Plugins.ReviewComment`

A review comment.

| Member | Description |
|---|---|
| `ReviewComment(string Author, string Body, DateTimeOffset CreatedAt)` | A review comment. |
| `string Author { get; init; }` | Who wrote it. |
| `string Body { get; init; }` | The text (Markdown). |
| `DateTimeOffset CreatedAt { get; init; }` | When. |
| `string? Url { get; init; }` | The comment's page. |

### `ReviewThread`

`record Codalus.Plugins.ReviewThread`

Review comments on a line of a file: `ReviewThread.Path` is relative to the repository (forward slashes), and `ReviewThread.Line` is one-based in the pull request's version of the file.

| Member | Description |
|---|---|
| `ReviewThread(string Id, string Path, int Line, IReadOnlyList<ReviewComment> Comments)` | Review comments on a line of a file: `ReviewThread.Path` is relative to the repository (forward slashes), and `ReviewThread.Line` is one-based in the pull request's version of the file. |
| `IReadOnlyList<ReviewComment> Comments { get; init; }` | The comments, oldest first. |
| `string Id { get; init; }` | The thread's id on the host. |
| `bool IsOutdated { get; init; }` | Whether the code it is on has changed since. |
| `bool IsResolved { get; init; }` | Whether it was resolved. |
| `int Line { get; init; }` | The (last) line commented on. |
| `string Path { get; init; }` | The file. |
| `int? StartLine { get; init; }` | The first line, for comments on a range. |
| `string? Url { get; init; }` | The thread's page. |

### `SettingsChangedEventArgs`

`sealed class Codalus.Plugins.SettingsChangedEventArgs` : EventArgs

Arguments of `ISettings.Changed`.

| Member | Description |
|---|---|
| `SettingsChangedEventArgs(IReadOnlyCollection<string> keys)` | Arguments of `ISettings.Changed`. |
| `IReadOnlyCollection<string> Keys { get; }` | The keys whose effective value changed. |
| `bool Affects(string keyOrSection)` | True if `keyOrSection` or any key under it changed. |

### `SourceControlGroup`

`record Codalus.Plugins.SourceControlGroup`

A group of a provider's files: "Changes", "Staged", "Conflicts"…

| Member | Description |
|---|---|
| `SourceControlGroup(string Id, string Label, IReadOnlyList<SourceControlResource> Resources)` | A group of a provider's files: "Changes", "Staged", "Conflicts"… |
| `string Id { get; init; }` | The group's id. |
| `string Label { get; init; }` | What the user sees. |
| `IReadOnlyList<SourceControlResource> Resources { get; init; }` | Its files. |

### `SourceControlResource`

`record Codalus.Plugins.SourceControlResource`

A file a source control provider lists: its path and status (`M`odified, `A`dded, `D`eleted, `?` untracked…).

| Member | Description |
|---|---|
| `SourceControlResource(string Path, string Status, string? Tooltip = null)` | A file a source control provider lists: its path and status (`M`odified, `A`dded, `D`eleted, `?` untracked…). |
| `string Path { get; init; }` | The file's full path. |
| `string Status { get; init; }` | One or two letters, as the provider's tool shows them. |
| `string? Tooltip { get; init; }` | More about the change, shown on hover. |

### `SourceControlState`

`record Codalus.Plugins.SourceControlState`

What a provider shows: its groups, and its commit box's hint and button.

| Member | Description |
|---|---|
| `SourceControlState(IReadOnlyList<SourceControlGroup> Groups)` | What a provider shows: its groups, and its commit box's hint and button. |
| `string? AcceptLabel { get; init; }` | The commit button's label ("Commit", "Submit", "Check In"). |
| `IReadOnlyList<SourceControlGroup> Groups { get; init; }` | The groups, in order. |
| `string? InputPlaceholder { get; init; }` | The commit box's hint ("Message (Ctrl+Enter to commit)"). |
| `string? Status { get; init; }` | A short status (the branch, a changelist). |

### `StatusBarAlignment`

`enum Codalus.Plugins.StatusBarAlignment`

Side of the status bar.

| Member | Description |
|---|---|
| `Left = 0` | Left side; higher priority is further left. |
| `Right = 1` | Right side; higher priority is further right. |

### `StatusCheck`

`record Codalus.Plugins.StatusCheck`

One check, job or build: `StatusCheck.State` as in `BranchStatus` (plus `skipped` and `cancelled`).

| Member | Description |
|---|---|
| `StatusCheck(string Name, string State)` | One check, job or build: `StatusCheck.State` as in `BranchStatus` (plus `skipped` and `cancelled`). |
| `string? Description { get; init; }` | A short description. |
| `string Name { get; init; }` | Its name. |
| `string State { get; init; }` | Its state. |
| `string? Url { get; init; }` | Its page. |

### `TerminalOptions`

`record Codalus.Plugins.TerminalOptions`

What `ITerminals.CreateAsync` opens.

| Member | Description |
|---|---|
| `TerminalOptions()` | Creates one. |
| `IReadOnlyList<string> Arguments { get; init; }` | The program's arguments. |
| `IReadOnlyDictionary<string, string>? Environment { get; init; }` | Environment variables added to the terminal's. |
| `string? Name { get; init; }` | The tab's name; default: the profile's or the program's name. |
| `string? Profile { get; init; }` | A terminal profile id (`pwsh`, `bash`, `dotnet`, …); null for the user's default. Ignored with `TerminalOptions.Program`. |
| `string? Program { get; init; }` | A program to run instead of a shell (a path, or a name found on `PATH`). |
| `bool ReportOutput { get; init; }` | Whether `ITerminal.Output` is raised. Off by default: output is only shown in the terminal. |
| `bool Show { get; init; }` | Whether to show the Terminal window with it (default true). |
| `string? WorkingDirectory { get; init; }` | The working directory; default: the solution's folder, else the user's home. |

### `TerminalOutputEventArgs`

`sealed class Codalus.Plugins.TerminalOutputEventArgs` : EventArgs

Arguments of `ITerminal.Output`.

| Member | Description |
|---|---|
| `TerminalOutputEventArgs(string text)` | Arguments of `ITerminal.Output`. |
| `string Text { get; }` | The text, escape sequences included. |

### `TestAttachment`

`record Codalus.Plugins.TestAttachment`

A file a run produced. `TestAttachment.Uri` is a `file:` URI.

| Member | Description |
|---|---|
| `TestAttachment(string Uri, string? DisplayName = null)` | A file a run produced. `TestAttachment.Uri` is a `file:` URI. |
| `string? DisplayName { get; init; }` | What the Test Explorer calls it (default: the file's name). |
| `string Uri { get; init; }` | The file, as a `file:` URI. |

### `TestCandidate`

`record Codalus.Plugins.TestCandidate`

A test class or method found in source. `TestCandidate.Type` is the managed type name runners report (`Namespace.Outer+Inner`); `TestCandidate.Name` is the method name, or the class's simple name. `TestCandidate.Parameterized` marks theories and data-driven tests, whose cases only a runner can list.

| Member | Description |
|---|---|
| `TestCandidate(string Kind, string Name, string Type, string? Namespace, TestRange Range, TestRange SelectionRange, bool Parameterized)` | A test class or method found in source. `TestCandidate.Type` is the managed type name runners report (`Namespace.Outer+Inner`); `TestCandidate.Name` is the method name, or the class's simple name. `TestCandidate.Parameterized` marks theories and data-driven tests, whose cases only a runner can list. |
| `const string ClassKind = "class"` | `TestCandidate.Kind` of a test class. |
| `const string MethodKind = "method"` | `TestCandidate.Kind` of a test method. |
| `string Kind { get; init; }` | `TestCandidate.ClassKind` or `TestCandidate.MethodKind`. |
| `string Name { get; init; }` | The method's name, or the class's simple name. |
| `string? Namespace { get; init; }` | The namespace (null for the global namespace). |
| `bool Parameterized { get; init; }` | Whether it is a theory or data-driven test, whose cases only a runner can list. |
| `TestRange Range { get; init; }` | The declaration's range. |
| `TestRange SelectionRange { get; init; }` | The name's range, where the editor's glyph goes. |
| `string Type { get; init; }` | The managed type name runners report (`Namespace.Outer+Inner`). |

### `TestItem`

`sealed class Codalus.Plugins.TestItem`

A test, or a group of tests (project, namespace, class, theory). Property changes and changes to `TestItem.Children` are sent to the shell once the item belongs to a controller.

| Member | Description |
|---|---|
| `TestItem(string id, string label)` | A new item; it is sent to the shell once added to a controller's tree. |
| `TestItemCollection Children { get; }` | The item's children. |
| `string? Description { get; set; }` | Muted text after the label. |
| `string? Error { get; set; }` | A problem loading this item (for example a discovery failure), shown on it. |
| `string Id { get; }` | Unique within the controller. |
| `string Label { get; set; }` | The text shown in the Test Explorer. |
| `TestItem? Parent { get; }` | The item this one is a child of, or null for roots and detached items. |
| `TestRange? Range { get; set; }` | The declaration's range in `TestItem.Uri`; its first line gets the editor glyph. |
| `string? SortText { get; set; }` | Sorts the item among its siblings instead of the label. |
| `IReadOnlyList<TestTrait> Traits { get; set; }` | Traits and categories, for grouping and `trait:` filters. |
| `string? Uri { get; set; }` | The document that declares the test (a `file:` URI). |
| `event EventHandler? Changed` | Raised after a property changed. |

### `TestItemCollection`

`sealed class Codalus.Plugins.TestItemCollection` : IEnumerable, IEnumerable<TestItem>, IReadOnlyCollection<TestItem>

The children of a `TestItem` (or a controller's roots), keyed by id. Thread-safe.

| Member | Description |
|---|---|
| `int Count { get; }` | How many items there are. |
| `event EventHandler<TestItemCollectionChangedEventArgs>? Changed` | Raised after items were added, replaced or removed. |
| `void Add(TestItem item)` | Adds `item`, replacing an existing item with the same id. |
| `static TestItemCollection CreateRoot()` | A collection without an owner item (a controller's roots). |
| `TestItem? Get(string id)` | The direct child with `id`, or null. |
| `IEnumerator<TestItem> GetEnumerator()` | A snapshot of the items, in insertion order. |
| `bool Remove(string id)` | Removes the child with `id` (and its subtree). Returns false when there was none. |
| `void Replace(IEnumerable<TestItem> items)` | Makes the collection hold exactly `items`. Items whose id is already present and that are the same instance stay; others are added or removed. |

### `TestItemCollectionChangedEventArgs`

`sealed class Codalus.Plugins.TestItemCollectionChangedEventArgs` : EventArgs

Arguments of `TestItemCollection.Changed`.

| Member | Description |
|---|---|
| `TestItemCollectionChangedEventArgs(IReadOnlyList<TestItem> added, IReadOnlyList<TestItem> removed)` | Arguments of `TestItemCollection.Changed`. |
| `IReadOnlyList<TestItem> Added { get; }` | Items added (or replacing an item with the same id). |
| `IReadOnlyList<TestItem> Removed { get; }` | Items removed (or replaced). |

### `TestMessage`

`record Codalus.Plugins.TestMessage`

A failure or error message. `TestMessage.Uri` and `TestMessage.Range` locate it in source.

| Member | Description |
|---|---|
| `TestMessage(string Message)` | A failure or error message. `TestMessage.Uri` and `TestMessage.Range` locate it in source. |
| `string? Actual { get; init; }` | The actual value of a failed assertion. |
| `string? Expected { get; init; }` | The expected value of a failed assertion, when the framework reports it. |
| `string Message { get; init; }` | The failure's or error's text. |
| `TestRange? Range { get; init; }` | The failure's location in `TestMessage.Uri`. |
| `string? StackTrace { get; init; }` | The stack trace as the runner printed it; the shell makes `in file:line N` frames clickable. |
| `string? Uri { get; init; }` | The document of the failure (a `file:` URI). |

### `TestRange`

`record Codalus.Plugins.TestRange`

A zero-based range in a document (LSP style).

| Member | Description |
|---|---|
| `TestRange(int StartLine, int StartCharacter, int EndLine, int EndCharacter)` | A zero-based range in a document (LSP style). |
| `int EndCharacter { get; init; }` | The character after the range on it (zero-based). |
| `int EndLine { get; init; }` | The last line (zero-based). |
| `int StartCharacter { get; init; }` | The first character on it (zero-based). |
| `int StartLine { get; init; }` | The first line (zero-based). |

### `TestRunProfileKind`

`enum Codalus.Plugins.TestRunProfileKind`

What a run profile does.

| Member | Description |
|---|---|
| `Coverage = 2` | Runs the tests and collects code coverage. |
| `Debug = 1` | Runs the tests under the debugger. |
| `Run = 0` | Runs the tests. |

### `TestRunRequest`

`record Codalus.Plugins.TestRunRequest`

What to run. `TestRunRequest.Include` null means every test of the controller; tests under an included item are included unless they (or an ancestor) are in `TestRunRequest.Exclude`.

| Member | Description |
|---|---|
| `TestRunRequest(IReadOnlyList<TestItem>? Include, IReadOnlyList<TestItem> Exclude, ITestRunProfile? Profile)` | What to run. `TestRunRequest.Include` null means every test of the controller; tests under an included item are included unless they (or an ancestor) are in `TestRunRequest.Exclude`. |
| `IReadOnlyList<TestItem> Exclude { get; init; }` | Items left out, with the tests under them. |
| `IReadOnlyList<TestItem>? Include { get; init; }` | The items to run, with the tests under them; null for every test of the controller. |
| `ITestRunProfile? Profile { get; init; }` | The run profile the user chose (run, debug or coverage). |

### `TestTrait`

`record Codalus.Plugins.TestTrait`

A trait (xUnit), category (NUnit, MSTest) or property; `TestTrait.Value` is null for plain categories.

| Member | Description |
|---|---|
| `TestTrait(string Name, string? Value)` | A trait (xUnit), category (NUnit, MSTest) or property; `TestTrait.Value` is null for plain categories. |
| `string Name { get; init; }` | The trait's or category's name. |
| `string? Value { get; init; }` | Its value; null for a plain category. |

### `TreeDataChangedEventArgs<T>`

`sealed class Codalus.Plugins.TreeDataChangedEventArgs<T>` : EventArgs

Arguments of `ITreeDataProvider.Changed`.

| Member | Description |
|---|---|
| `TreeDataChangedEventArgs<T>(T element)` | Arguments of `ITreeDataProvider.Changed`. |
| `T Element { get; }` | The element whose children changed, or default for the whole tree. |

### `TreeItem`

`record Codalus.Plugins.TreeItem`

How an element of a tree view is displayed. `TreeItem.Icon` names an icon of the shell's built-in set.

| Member | Description |
|---|---|
| `TreeItem(string Label)` | How an element of a tree view is displayed. `TreeItem.Icon` names an icon of the shell's built-in set. |
| `TreeItemCollapsibleState Collapsible { get; init; }` | Whether the item has children. |
| `TreeItemCommand? Command { get; init; }` | Command executed when the item is activated. |
| `string? Description { get; init; }` | Secondary text shown after the label, dimmed. |
| `string? Icon { get; init; }` | A built-in icon name, e.g. `file`, `folder`, `gear`, `play`, `info`. |
| `string Label { get; init; }` | The main text. |
| `string? Tooltip { get; init; }` | Hover text. |

### `TreeItemCollapsibleState`

`enum Codalus.Plugins.TreeItemCollapsibleState`

Whether a tree item can be expanded, and its initial state.

| Member | Description |
|---|---|
| `Collapsed = 1` | Has children; initially collapsed. |
| `Expanded = 2` | Has children; initially expanded. |
| `None = 0` | A leaf. |

### `TreeItemCommand`

`record Codalus.Plugins.TreeItemCommand`

A command run when a tree item is activated (double-click or Enter).

| Member | Description |
|---|---|
| `TreeItemCommand(string CommandId, params object?[] Args)` | A command run when a tree item is activated (double-click or Enter). |
| `object?[] Args { get; init; }` | JSON-serializable arguments. |
| `string CommandId { get; init; }` | The command to execute. |

### `TypeOutline`

`record Codalus.Plugins.TypeOutline`

A type of a document, from syntax.

| Member | Description |
|---|---|
| `TypeOutline(string Name, string? Namespace, string NestedName, bool IsStatic, bool HasParameterlessConstructor, IReadOnlyList<MethodOutline> Methods, string? SelectedMethod)` | A type of a document, from syntax. |
| `bool HasParameterlessConstructor { get; init; }` | Whether `new T()` compiles. |
| `bool IsStatic { get; init; }` | Whether it is a static class. |
| `IReadOnlyList<MethodOutline> Methods { get; init; }` | Its public and internal methods. |
| `string Name { get; init; }` | The type's name. |
| `string? Namespace { get; init; }` | Its namespace, or null. |
| `string NestedName { get; init; }` | `Outer.Inner` for nested types; the name otherwise. |
| `string? SelectedMethod { get; init; }` | The method at the position, if any. |

### `ViewEvent`

`record Codalus.Plugins.ViewEvent`

What the user did in a declarative view: `click` (a button), `change` (a text box's text when confirmed, a check box, a dropdown: `ViewEvent.Value`), `select` (a table row, `ViewEvent.Row`) or `activate` (a table row double-clicked or Enter).

| Member | Description |
|---|---|
| `ViewEvent(string ElementId, string Kind, string? Value = null, int? Row = null)` | What the user did in a declarative view: `click` (a button), `change` (a text box's text when confirmed, a check box, a dropdown: `ViewEvent.Value`), `select` (a table row, `ViewEvent.Row`) or `activate` (a table row double-clicked or Enter). |
| `string ElementId { get; init; }` | The element's id. |
| `string Kind { get; init; }` | What happened. |
| `int? Row { get; init; }` | The table row, for a select or an activate. |
| `string? Value { get; init; }` | The new value, for a change. |

### `ViewNode`

`record Codalus.Plugins.ViewNode`

An element of a declarative view (D2, API 1.0, M22a). A plugin describes its view as a tree of these and the IDE renders it with its own controls, in its theme, with no plugin UI code. Use the factory methods; `ViewNode.Kind` is one of `stack`, `text`, `button`, `textbox`, `checkbox`, `dropdown`, `table`, `toolbar` and `separator`. Elements the user acts on need an `ViewNode.Id`.

| Member | Description |
|---|---|
| `ViewNode(string Kind)` | An element of a declarative view (D2, API 1.0, M22a). A plugin describes its view as a tree of these and the IDE renders it with its own controls, in its theme, with no plugin UI code. Use the factory methods; `ViewNode.Kind` is one of `stack`, `text`, `button`, `textbox`, `checkbox`, `dropdown`, `table`, `toolbar` and `separator`. Elements the user acts on need an `ViewNode.Id`. |
| `bool? Checked { get; init; }` | A check box's state. |
| `IReadOnlyList<ViewNode>? Children { get; init; }` | A stack's or a toolbar's elements. |
| `IReadOnlyList<string>? Columns { get; init; }` | A table's column headers. |
| `bool Disabled { get; init; }` | The element is shown but can't be used. |
| `string? Id { get; init; }` | The element's id within its view: events name it, and input survives updates by it. |
| `string Kind { get; init; }` | What the element is. |
| `IReadOnlyList<string>? Options { get; init; }` | A dropdown's options. |
| `string? Orientation { get; init; }` | A stack's direction: `vertical` (default) or `horizontal`. |
| `string? Placeholder { get; init; }` | A text box's hint while it's empty. |
| `IReadOnlyList<IReadOnlyList<string>>? Rows { get; init; }` | A table's rows, one string per column. |
| `string? Style { get; init; }` | Text: `heading`, `muted` or `code`; button: `primary`. |
| `string? Text { get; init; }` | A text's text, a button's or a check box's label. |
| `string? Value { get; init; }` | A text box's text, or a dropdown's selected option. |
| `static ViewNode Button(string id, string text, bool primary = false)` | A button: a `click` event. |
| `static ViewNode CheckBox(string id, string text, bool isChecked)` | A check box: a `change` event with `true` or `false`. |
| `static ViewNode Dropdown(string id, IReadOnlyList<string> options, string? selected = null)` | A dropdown: a `change` event with the chosen option. |
| `static ViewNode Heading(string text)` | A heading. |
| `static ViewNode Label(string text, string? style = null)` | A text, plain or `muted` or `code`. |
| `static ViewNode Row(params ViewNode[] children)` | Elements side by side. |
| `static ViewNode Separator()` | A line between elements. |
| `static ViewNode Stack(params ViewNode[] children)` | Elements one under the other. |
| `static ViewNode Table(string id, IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)` | A table: `select` and `activate` events with the row's index. |
| `static ViewNode TextBox(string id, string? value = null, string? placeholder = null)` | A text box: a `change` event with its text when the user presses Enter or leaves it. |
| `static ViewNode Toolbar(params ViewNode[] buttons)` | A row of buttons. |

### `VisualizerView`

`record Codalus.Plugins.VisualizerView`

What a visualizer shows: text in a read-only editor (colored by `VisualizerView.LanguageId`) or a table.

| Member | Description |
|---|---|
| `VisualizerView()` | Creates one. |
| `IReadOnlyList<string>? Columns { get; init; }` | A table's column headers. |
| `string? LanguageId { get; init; }` | The text's language (`json`, `xml`, `markdown`, …); null for plain text. |
| `IReadOnlyList<IReadOnlyList<string>>? Rows { get; init; }` | A table's rows, a cell per column. |
| `string? Text { get; init; }` | Text to show in a read-only editor. |
| `static VisualizerView FromTable(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<string>> rows)` | A table. |
| `static VisualizerView FromText(string text, string? languageId = null)` | Text, colored by `languageId`. |

### `WorkItem`

`record Codalus.Plugins.WorkItem`

An issue or work item.

| Member | Description |
|---|---|
| `WorkItem(string Number, string Title, string Reference, string WebUrl)` | An issue or work item. |
| `string? AssignedTo { get; init; }` | Who it is assigned to. |
| `string Number { get; init; }` | Its number. |
| `string Reference { get; init; }` | How a commit message mentions it, e.g. `#123`. |
| `string? State { get; init; }` | Its state (open, Active, To Do…). |
| `string Title { get; init; }` | Its title. |
| `string? Type { get; init; }` | Its type (Issue, Bug, Task, User Story…). |
| `string WebUrl { get; init; }` | Its page. |

### `WorkItemQuery`

`record Codalus.Plugins.WorkItemQuery`

Which work items to list.

| Member | Description |
|---|---|
| `WorkItemQuery()` | Creates one. |
| `bool AssignedToMe { get; init; }` | Only those assigned to the user. |
| `int Limit { get; init; }` | At most this many. |
| `string? Text { get; init; }` | A number or words to match (commit message completion); null for any. |

### `WorkspacePackage`

`record Codalus.Plugins.WorkspacePackage`

A NuGet package of a project.

| Member | Description |
|---|---|
| `WorkspacePackage(string Id, string? RequestedVersion, string? ResolvedVersion, bool IsTransitive, IReadOnlyList<string> TargetFrameworks)` | A NuGet package of a project. |
| `string Id { get; init; }` | The package id. |
| `bool IsTransitive { get; init; }` | Whether it is only a dependency of other packages or projects. |
| `string? RequestedVersion { get; init; }` | The version or range asked for (top-level packages only). |
| `string? ResolvedVersion { get; init; }` | The restored version, or null when the project isn't restored. |
| `IReadOnlyList<string> TargetFrameworks { get; init; }` | The target frameworks that use it. |

### `WorkspaceProject`

`record Codalus.Plugins.WorkspaceProject`

A project of the solution. `WorkspaceProject.State` is `loading`, `loaded`, `failed`… (as in the Solution Explorer). `WorkspaceProject.TestPlatform` is `vstest`, `mtp` (Microsoft.Testing.Platform) or null. `WorkspaceProject.CompileFiles` are absolute paths.

| Member | Description |
|---|---|
| `WorkspaceProject(string Path, string Name, string State, IReadOnlyList<string> TargetFrameworks, string? OutputType, bool IsTestProject, string? TestPlatform, IReadOnlyList<string> CompileFiles, IReadOnlyList<string> ProjectReferences)` | A project of the solution. `WorkspaceProject.State` is `loading`, `loaded`, `failed`… (as in the Solution Explorer). `WorkspaceProject.TestPlatform` is `vstest`, `mtp` (Microsoft.Testing.Platform) or null. `WorkspaceProject.CompileFiles` are absolute paths. |
| `IReadOnlyList<string> CompileFiles { get; init; }` | Its C# files, as absolute paths. |
| `bool IsTestProject { get; init; }` | Whether it is a test project. |
| `string Name { get; init; }` | Its name. |
| `string? OutputType { get; init; }` | Its `OutputType` (`Exe`, `Library`…). |
| `string Path { get; init; }` | The project file. |
| `IReadOnlyList<string> ProjectReferences { get; init; }` | The project files it references. |
| `string State { get; init; }` | `loading`, `loaded`, `failed`… as in the Solution Explorer. |
| `IReadOnlyList<string> TargetFrameworks { get; init; }` | Its target frameworks (`net10.0`…). |
| `string? TestPlatform { get; init; }` | `vstest`, `mtp` (Microsoft.Testing.Platform) or null. |

### `WorkspaceSolution`

`record Codalus.Plugins.WorkspaceSolution`

The open solution. `WorkspaceSolution.SdkPath` is the .NET SDK folder MSBuild uses (it contains `vstest.console.dll`). Projects are evaluated only when `WorkspaceSolution.Trusted`.

| Member | Description |
|---|---|
| `WorkspaceSolution(string Path, bool Trusted, string? Configuration, string? Platform, string? SdkPath, IReadOnlyList<WorkspaceProject> Projects)` | The open solution. `WorkspaceSolution.SdkPath` is the .NET SDK folder MSBuild uses (it contains `vstest.console.dll`). Projects are evaluated only when `WorkspaceSolution.Trusted`. |
| `string? Configuration { get; init; }` | The active configuration (`Debug`, `Release`…). |
| `string Path { get; init; }` | The solution file, or the folder in folder mode. |
| `string? Platform { get; init; }` | The active platform (`Any CPU`…). |
| `IReadOnlyList<WorkspaceProject> Projects { get; init; }` | Its projects. |
| `string? SdkPath { get; init; }` | The .NET SDK folder MSBuild uses. |
| `bool Trusted { get; init; }` | Whether the user trusts its folder (only then are projects evaluated). |

### `IProposedApi`

`interface Codalus.Plugins.Proposed.IProposedApi`

Proposed APIs (ADR 0003): not yet covered by the versioning policy and free to change in any release. A plugin must set `"enableProposedApi": true`; third-party plugins also need a development build (`--enable-proposed-api`). Built-in plugins may use them in every build (ADR 0010). API 1.0 promoted every proposal that built-in plugins had proved (tests, workspace, build, debug, terminals, hosting, secrets and visualizers) to `IPluginContext`. New proposals start here.

### `IProposedApiProvider`

`interface Codalus.Plugins.Proposed.IProposedApiProvider`

Implemented by plugin contexts that can offer proposed APIs.

| Member | Description |
|---|---|
| `IProposedApi? Proposed { get; }` | The proposed APIs, or null when the plugin did not enable them. |

### `ProposedApiExtensions`

`static class Codalus.Plugins.Proposed.ProposedApiExtensions`

Access to proposed APIs from an `IPluginContext`.

| Member | Description |
|---|---|
| `static IProposedApi Proposed(IPluginContext context)` | The proposed APIs. Throws when the plugin's manifest doesn't set `enableProposedApi`. |


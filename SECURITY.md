# Security policy

## Supported versions

Codalus IDE is pre-1.0. Security fixes go into the latest release only.

## Reporting a vulnerability

**Please don't open a public issue for a security problem.** Report it privately through GitHub's
[private vulnerability reporting](https://github.com/stavroskasidis/codalus-ide/security/advisories/new)
(Security → Report a vulnerability). Include what you found, how to reproduce it, and the version and OS.

You can expect:
- an acknowledgement within 7 days
- an assessment and a plan within 30 days

We will credit you in the advisory unless you prefer otherwise.

## Scope

Codalus IDE runs code from the folders you open. For example:
- MSBuild evaluates project files and imports.
- Restore runs NuGet targets.
- Analyzers, source generators and plugins load into helper processes.

That is why the trust prompt exists. Opening an **untrusted** folder must not run code from it. Report any way around that as a vulnerability.

The following are also in scope:
- the plugin host's isolation and the plugin API's permission model
- the handling of files and paths
- anything that sends data off the machine without the user's consent. Codalus IDE has no telemetry, and crash reports are local and opt-in.

Vulnerabilities in third-party components (MSBuild, Roslyn, netcoredbg, git, Avalonia) should be reported to those projects. If one affects Codalus IDE, tell us too.

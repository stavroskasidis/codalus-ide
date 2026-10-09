# NuGet package ID prefix reservation (draft)

## Finding

On 2026-10-09, nuget.org already lists packages that use the `Daedalus.` prefix, owned by other people:

| Package | Owner | Downloads (approximate) |
|---|---|---|
| `Daedalus` | donald-hanson | (the package family's root) |
| `Daedalus.Events.EventBus.Memory`, `Daedalus.Events.EventStore.*` | donald-hanson | 6,900 |
| `Daedalus.Commands.CommandBus.Memory` | donald-hanson | 6,400 |
| `DaedalusCompiler`, `DaedalusLib`, `DaedalusCompilerIkarus` | kolarz3 | 1,000–2,000 |

NuGet's prefix reservation criteria say a prefix is only granted when existing packages under it belong to the requester, or their owners agree. A reservation of **`Daedalus.*`** would very likely be **refused**.

`DaedalusStudio` and `DaedalusStudio.*` are free. So is `Daedalus.Plugins.Abstractions`, but it can't be protected by a prefix.

## Recommendation

1. Reserve **`DaedalusStudio.*`**.
2. Publish the plugin SDK as `DaedalusStudio.Plugins.Abstractions`, and later templates as `DaedalusStudio.Templates`.
   - The C# root namespace can stay `Daedalus.*`: package IDs and namespaces don't have to match.
   - This changes the package ID the plugin docs mention (ADR 0003). Decide before the first plugin SDK release, while nobody depends on the ID.
3. Alternatively, ask the owner of the `Daedalus.*` packages for consent first. This is unlikely to succeed, and the prefix would still be shared.

## Request (to send to account@nuget.org once the repository is public)

> **Subject:** Package ID prefix reservation request: DaedalusStudio.*
>
> Hello,
>
> I'd like to reserve the package ID prefix **`DaedalusStudio.*`** for the nuget.org account **`<your nuget.org account>`**.
>
> Daedalus Studio is an open-source (MIT), cross-platform IDE for .NET: https://github.com/stavroskasidis/daedalus-studio. We will publish its plugin SDK and project templates under this prefix (for example `DaedalusStudio.Plugins.Abstractions`). Third-party plugin authors will reference these packages, so a verified prefix will protect them from look-alike packages.
>
> - Prefix: `DaedalusStudio.*`
> - Owner account(s): `<account>`
> - Project URL: https://github.com/stavroskasidis/daedalus-studio
> - License: MIT
> - No existing packages on nuget.org use this prefix.
>
> Thank you,
> `<name>`

## Before sending

- [ ] Create or choose the nuget.org account (an organization account is better for a project) and enable 2FA.
- [ ] Make the repository public. The request links to it.
- [ ] Decide the SDK's package ID (recommendation 2).

## What and why

<!-- What does this change, and why? Link the issue: "Fixes #123". -->

## How it was tested

<!-- Unit, integration or headless UI tests added or changed; manual checks on which OS. -->

## Checklist

- [ ] Tests cover the change, and `dotnet test --solution DaedalusStudio.slnx` passes locally
- [ ] No new warnings (builds treat warnings as errors); the shell still publishes with Native AOT
- [ ] New dependencies are MIT-compatible and recorded in `THIRD-PARTY-NOTICES.md`
- [ ] Docs updated (README, `docs/`, plugin API changelog for API changes), and an ADR for significant decisions
- [ ] No performance budget regressions (or the regression is justified above)
- [ ] Commits are signed off (`git commit -s`, DCO)

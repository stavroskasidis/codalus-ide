# BuildAndRun fixture

Exercises builds and Run Without Debugging (ADR 0006); tests copy it before building.

- `Greetings` is a library; `Hello` is a console app that references it. `Greetings` comes first in the solution, so the default startup project is the first *executable* one, `Hello`.
- `Hello` prints its arguments, the `GREETING` environment variable and its working directory, and exits with the code given by `--exit <n>`.
- `Hello/Properties/launchSettings.json` has two `Project` profiles (`Hello`: arguments and an environment variable; `Fails`: `--exit 3`) and an unsupported `IISExpress` one.

# Contributing

This repository is the Unity package root for `com.igp.custom-unity-cli`.

## Development

Agent instructions live in [`Skills~/cucli-develop-plugin/SKILL.md`](Skills~/cucli-develop-plugin/SKILL.md). Longer design notes live in [`docs~/`](docs~/). Host integration: [`Skills~/cucli-use-plugin/SKILL.md`](Skills~/cucli-use-plugin/SKILL.md).

```powershell
Tools/verify.ps1              # manifest, .meta coverage, command contract, offline Roslyn compile
Tools/verify.ps1 -WithUnity   # import into a throwaway project with no optional packages

./Tools/cucli.ps1 selftest -Tier AB                 # Core surface, live Editor worker
./Tools/cucli.ps1 selftest -Tier AB -IncludeExtended
```

`verify.ps1` is the build gate. `cucli selftest` is the behaviour gate. Do not start, focus or
otherwise disturb a running Editor to run the build gate.

## Commit messages

English only. Conventional Commits:

```text
type(scope): imperative summary in English
```

Allowed types: `feat`, `fix`, `docs`, `test`, `refactor`, `chore`, `perf`.
Do not write Chinese (or mixed-language) git messages in this repository.

## Package boundary

Project-specific commands belong in `Assets/Plugins/CustomUnityCli.Extensions` of the host project,
never inside this package. See [`docs~/self-iteration.md`](docs~/self-iteration.md).

# RepoRunner

Runs a repo against a named environment (LOCAL, UAT, PROD...) whose variables live in an encrypted vault.
Nothing is written to the repo and no Windows User or Machine variable is set: values go straight into the
environment of the process `rr` starts.

Design document: https://claude.ai/artifact/MSdACk61ieiLPEfTYhRUKN

## Build

```powershell
.\publish.ps1        # runs the tests, then writes artifacts\rr.exe
```

Put `artifacts` on your `PATH`, or copy `rr.exe` somewhere that already is.

## First use, inside a repo

```powershell
rr add --env UAT                                  # register the repo, detect run targets
rr import --from OpsPortal\.env --env UAT         # pull an existing file into the vault
rr vars                                           # what UAT resolves to (secrets masked)
rr run                                            # start the default target on UAT
rr watch --env LOCAL                              # dotnet watch on another environment
rr exec --env UAT -- dotnet ef database update    # any command with the environment
```

`rr help` lists every command.

## Things to know

- **A leftover `.env` wins.** `EnvHelper.LoadVariables` and DotNetEnv overwrite the process environment from
  the file. Delete the file after importing (`--delete-source` does it once the import is verified).
- **Launch profiles are skipped.** .NET targets run with `--no-launch-profile`, because variables in
  `launchSettings.json` would override the injected ones. The profile's URL is carried over as `ASPNETCORE_URLS`,
  and `ASPNETCORE_ENVIRONMENT` defaults to `Development` unless the vault sets it.
- **PROD is protected.** The first `rr set --env PROD` asks you to choose a passphrase; every later use of
  PROD asks for it. There is no cache yet, and no recovery if it is forgotten.
- **Back up the vault.** `rr vault export backup.rrb` writes a passphrase-encrypted bundle. The vault itself
  is tied to your Windows profile and is lost if the profile is rebuilt.
- **Shell variables are inherited** by default, with vault values on top. `--clean` passes only an allow-list.
- **Values are typed, not passed.** `rr set KEY` prompts, or reads one line from stdin, so values stay out of
  shell history.

The vault is `%LocalAppData%\RepoRunner\vault.db`. Set `RR_HOME` to use another folder.

## Layout

- `src/RepoRunner.Core` - vault, encryption, resolver, project detection, launcher
- `src/RepoRunner.Cli` - the `rr` command
- `tests/RepoRunner.Tests` - xUnit tests

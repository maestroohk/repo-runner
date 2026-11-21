# RepoRunner

Runs your repos against a named environment (LOCAL, UAT, PROD...) whose variables live in an encrypted vault,
shows what the app is doing while it runs, and works out from the code what each app does. By default an app
run from here cannot change anything on a real server: it runs in a sandbox.

Design document: https://claude.ai/artifact/MSdACk61ieiLPEfTYhRUKN

## Every day

1. Start RepoRunner from the Desktop shortcut (or `artifacts\app\RepoRunner.exe`). It opens in your browser.
2. Pick a repository on the home page, then the environment at the top (LOCAL, UAT...).
3. Press **Run** on a target. .NET apps start in the sandbox unless that environment is live (see below).
4. Use the app as usual. The tabs show what is happening:
   - **Variables**: the settings the app gets, kept encrypted.
   - **Output**: what the app prints. Errors show here first.
   - **Processes**: every page and API in the app, found by reading its code, each with an animated map of how
     information moves: which stored procedures it runs, which systems it calls, which settings it needs.
   - **Recordings**: record yourself using the app; every step, call and answer is kept. Failures are kept by
     themselves.
   - **Live map**: what the running app is doing right now.
   - **Code paths**: for developers, a page or API followed method by method.
5. The breadcrumbs at the top (All repositories › OpsPortal › Processes › ...) always lead back. Back and
   Forward in the browser work, and every screen has its own address you can bookmark.

## The sandbox

A .NET app started on an environment that is not live runs fenced in:

- **Lookups** (calls whose names say they only read: Get, Search, List...) go to the real server, so the app has
  its data and your access.
- **Changes** (save, approve, send, delete, or anything whose name does not say) never leave this computer. The
  app gets an answer it can read, and nothing happens anywhere.
- **Direct connections** to databases, Redis and other systems are refused before they are made. Microsoft
  sign-in is the one exception. A watchdog stops the app if anything else gets through.
- **When a lookup cannot be answered** (the server is down, nothing recorded), RepoRunner makes up an answer
  in the shape the app's code expects, filled with values the app is known to use. In the sandbox you hold every
  role and permission. Each made-up answer can be read and corrected from the run card (**See or change**) and
  is kept for later runs.
- **Your other apps**: if OpsApi also runs in the sandbox here, OpsPortal's OpsApi calls go to it, and the call's
  detail shows what happened inside OpsApi.

The run card counts all of this: looked up, changes held back, made-up answers, connections refused.

To let an environment reach real servers, press **Allow real servers** above the run targets (twice).
Protected environments (PROD) can never be made live. The command line refuses to start apps on a
sandbox-only environment, because it has no sandbox.

## Build

```powershell
.\publish.ps1            # runs the tests, then writes artifacts\rr.exe and artifacts\app\RepoRunner.exe
.\install-shortcut.ps1   # once: a Desktop shortcut to the window
```

Close the RepoRunner window before publishing: a running copy keeps its files locked.

## Command line

```powershell
rr add --env UAT                                  # register the repo, detect run targets
rr import --from OpsPortal\.env --env UAT         # pull an existing file into the vault
rr vars                                           # what UAT resolves to (secrets masked)
rr run                                            # start the default target (live environments only)
rr exec --env UAT -- dotnet ef database update    # any command with the environment (live environments only)
```

`rr help` lists every command.

## Things to know

- **A leftover `.env` wins.** `EnvHelper.LoadVariables` and DotNetEnv overwrite the process environment from
  the file. Delete the file after importing (`--delete-source` does it once the import is verified).
- **Launch profiles are skipped.** .NET targets run with `--no-launch-profile`; the profile's URL is carried over
  as `ASPNETCORE_URLS`, and `ASPNETCORE_ENVIRONMENT` defaults to `Development` unless the vault sets it.
- **PROD is protected** by a passphrase, asked once and remembered for 15 minutes. There is no recovery if it is
  forgotten.
- **Back up the vault.** `rr vault export backup.rrb` writes a passphrase-encrypted bundle. The vault is tied to
  your Windows profile and is lost if the profile is rebuilt.
- **Switching an app between live and sandbox:** apps keep their sign-in in the browser. If one behaves oddly
  after switching, sign out of it (or clear its site data) and sign in again.

The vault is `%LocalAppData%\RepoRunner\vault.db`; recordings, scans and kept sandbox answers sit beside it.
Set `RR_HOME` to use another folder.

## Layout

- `src/RepoRunner.Core` - vault, encryption, resolver, detection, launcher, code index, process discovery,
  made-up data
- `src/RepoRunner.App` - the window (Blazor), sessions, the sandbox stand-in, recordings
- `src/RepoRunner.StartupHook` - injected into .NET apps: records what they do, guards their connections
- `src/RepoRunner.Cli` - the `rr` command
- `tests/RepoRunner.Tests`

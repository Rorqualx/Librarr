# ServiceHelpers/ — Windows service helpers

Two tiny exe projects that install / uninstall Librarr as a Windows
service by running `Readarr.Console.exe /i` and `Readarr.Console.exe /u`
from the folder they sit in. They are NOT part of the running app.

## Projects

- `ServiceInstall/` (`ServiceInstall.csproj`) — runs `Readarr.Console.exe /i`.
- `ServiceUninstall/` (`ServiceUninstall.csproj`) — runs `Readarr.Console.exe /u`.

Both exit with the exit code of the `Readarr.Console.exe` they ran (`2` if
it is not beside them, `5` if they are somehow running unelevated).

## Who uses them

A person, by double-clicking. They ship in the Windows zip and in the
installer's `bin` folder (`build.sh` strips them from the Linux and macOS
packages only), for someone running the portable zip who wants a service
without opening an elevated prompt.

The Inno Setup installer does **not** call them — `readarr.iss` runs
`Readarr.Console.exe /i` itself, already elevated.

## Elevation

Service registration requires admin rights. Each project embeds
`app.manifest` (`requestedExecutionLevel level="requireAdministrator"`)
through `<ApplicationManifest>` in its csproj, so Windows shows the UAC
prompt on launch. Until that property was added the manifest sat in the
folder unreferenced: the helpers started unelevated, failed their own
administrator check, and — being `WinExe`, with no console to print to —
exited without a word.

Both are exercised by the "Windows smoke" CI job
(`tests/e2e/windows-smoke.ps1`, stage `service`).

## Output

Both project names are explicitly mapped to `Exe` in
`../Directory.Build.props` (`ReadarrOutputType`), so they publish into
the same folder as the app.

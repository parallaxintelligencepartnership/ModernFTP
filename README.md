# ModernFTP

## What it is

ModernFTP is a small FTP and FTPS server for Windows 11, a clean room rewrite of the classic TYPSoft FTP Server 1.10. It keeps the simple idea of the original (users, home folders, permissions, a log you can watch) and brings it up to date: FTPS, safe defaults, config stored in your profile, and a protocol engine tested against real clients.

It comes in three programs that share one engine:

| Program | What it is |
|---|---|
| `ModernFTP.exe` | The Windows app: tray icon, live log, sessions, setup and user windows. |
| `modernftp.exe` | The command line host. Also installs the Windows service and adds the firewall rules. |
| `modernftp-service.exe` | The Windows service host, started by the Service Control Manager. |

Status: version 0.1.0, first release. It is licensed for noncommercial use, see License below.

## Download

Get the zip from the Releases page of the GitHub repository (`parallaxintelligencepartnership/ModernFTP`). There are two:

| Zip | Pick it when |
|---|---|
| `ModernFTP-<version>-win-x64.zip` | You have the .NET 10 Desktop Runtime installed, or do not mind installing it. Small download. |
| `ModernFTP-<version>-win-x64-portable.zip` | You want nothing to install. Includes the .NET runtime, so it is larger. Contains a `portable` marker file, so config lives beside the exe. |

Unzip anywhere and run `ModernFTP.exe`. There is no installer. `SHA256SUMS.txt` on the release lists the checksum of each zip.

## Windows SmartScreen

ModernFTP is not code signed, so Windows SmartScreen may show "Windows protected your PC" the first time you run it. Click "More info", then "Run anyway". If you downloaded the zip in a browser, you can also right click the zip, open Properties and tick "Unblock" before unzipping.

## First run

The config is a `config.json` file.

| How you run it | Where the config lives |
|---|---|
| App or console, normal zip | `%AppData%\ModernFTP\config.json` |
| Portable zip (a file named `portable` beside the exe) | Beside the exe |
| Windows service | `%ProgramData%\ModernFTP\config.json` |

The app starts with defaults and writes the file the first time you save Setup. Open Setup to set the port, passive port range and limits, and Users to add accounts. Anonymous access is off by default.

To make any other zip portable, create an empty file named `portable` next to the exe.

## Firewall

Windows Firewall blocks incoming connections until you allow them. Either:

- In the app, open Setup and click "Add Windows Firewall rules" in the Application group. Windows asks for administrator approval. Save the setup first, because the rules use the saved port and passive range.
- Or, from an elevated prompt, run `modernftp firewall add`. Add `--config <path>` to use a config other than the default, and `--program <exe>` to tie the control port rule to a different exe (the default is `modernftp-service.exe`).

This creates two rules: "ModernFTP control" (the control port, for the exe) and "ModernFTP passive" (the passive port range). `modernftp firewall remove` deletes both.

## Run as a Windows service

From an elevated prompt, in the folder that holds the exes:

```
modernftp service install
modernftp service start
modernftp service status
modernftp service stop
modernftp service uninstall
```

The service is called ModernFTP, starts automatically, and runs as NT AUTHORITY\NetworkService. It reads `%ProgramData%\ModernFTP\config.json`; use `modernftp service install --config <path>` for another file. Create that file before you start the service (copy one from the app, or write one by hand, see the sample below).

Give the service account write access to the config folder, so it can write its log and its self signed certificate:

```
icacls "%ProgramData%\ModernFTP" /grant "NT AUTHORITY\NetworkService:(OI)(CI)M"
```

Logs go to the Windows Event Log under the source "ModernFTP" (start, stop and errors) and to a daily text log beside the config, `modernftp-service-yyyyMMdd.log`, which keeps 14 days.

Do not run the app and the service on the same port at the same time.

## Migrating from TYPSoft FTP Server

Point the importer at the folder that holds the old `config.ini` and `users.ini`:

```
modernftp import-typsoft --from "C:\Program Files (x86)\TYPSoft FTP Server" --to "%AppData%\ModernFTP\config.json"
```

Add `--force` to overwrite an existing config. Passwords are not migrated, because the old format cannot be trusted: every imported user must have a new password set in the Users window (or with `modernftp hash-password`) before it can log in. Anonymous access stays off by default.

## FTPS

FTPS (explicit TLS, `AUTH TLS`) is on by default. With no certificate configured, ModernFTP generates a self signed one in the config folder, and clients will ask you to trust it. To use your own certificate, set `tls.certificatePath` (a PFX file) and `tls.certificatePassword` in `config.json`. Set `tls.enabled` to false to turn FTPS off.

## Building from source

You need the .NET 10 SDK (10.0.401 or newer) and, for the conformance tests, `curl` on your PATH.

```
dotnet restore ModernFTP.sln --locked-mode
dotnet build ModernFTP.sln
dotnet test ModernFTP.sln
```

The Windows projects (app and service) build on macOS and Linux too, but only run on Windows. A minimal `config.json`:

```json
{
  "port": 2121,
  "passivePortMin": 50000,
  "passivePortMax": 50999,
  "users": [
    {
      "username": "matt",
      "passwordHash": "<from hash-password>",
      "passwordSalt": "<from hash-password>",
      "passwordIterations": 600000,
      "homeDirectory": "C:\\FTP\\matt",
      "permissions": { "download": true, "list": true, "upload": true }
    }
  ]
}
```

Run the console host without installing anything:

```
dotnet run --project src/ModernFTP.Host.Console -- hash-password
dotnet run --project src/ModernFTP.Host.Console -- check-config --config ./config.json
dotnet run --project src/ModernFTP.Host.Console -- serve --config ./config.json
```

| Project | What it is |
|---|---|
| `src/ModernFTP.Engine` | The FTP server itself. Cross platform. |
| `src/ModernFTP.Config` | The config model, validation, config folder and certificate. Cross platform. |
| `src/ModernFTP.Host.Console` | The `modernftp` command line host. |
| `src/ModernFTP.Host.Service` | The Windows service host, `modernftp-service.exe`. |
| `src/ModernFTP.App` | The Windows app (WPF with the Fluent theme). |
| `tests/` | Unit tests, a regression test for each known TYPSoft exploit, and curl driven conformance tests. |

Releases are built by `.github/workflows/release.yml` when a tag such as `v0.1.0` is pushed. The tag must equal `v` plus the version in `Directory.Build.props`.

## License

PolyForm Noncommercial License 1.0.0. See `LICENSE.md` and `NOTICE.md`. Copyright Parallax Intelligence Partnership, LLC.

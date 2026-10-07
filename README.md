# ModernFTP

ModernFTP is a small FTP server for Windows 11, a clean room rewrite of the classic TYPSoft FTP Server 1.10. It keeps the simple idea of the original (users, home folders, permissions, a log you can watch) and brings it up to date: FTPS, safe defaults, config stored in your profile, and a protocol engine tested against real clients.

**Status: pre-alpha, phase 0.** The FTP engine and a console host work and are tested. The Windows app is a placeholder window. Do not expose it to the internet yet.

## What is in the box

| Project | What it is |
|---|---|
| `src/ModernFTP.Engine` | The FTP server itself: sessions, commands, path jail, permissions, limits, FTPS, events. Cross platform. |
| `src/ModernFTP.Config` | The `config.json` model, validation, config folder and the self signed certificate. Cross platform. |
| `src/ModernFTP.Host.Console` | The `modernftp` command line host. Runs on Windows, macOS and Linux. |
| `src/ModernFTP.App` | The Windows app (WPF with the Fluent theme). A placeholder for now. |
| `tests/ModernFTP.Engine.Tests` | Unit tests plus one regression test for each known TYPSoft exploit. |
| `tests/ModernFTP.Conformance` | Drives the server with the system `curl`. |

## Build and test

You need the .NET 10 SDK (10.0.401 or newer) and, for the conformance tests, `curl` on your PATH.

```sh
dotnet restore ModernFTP.sln --locked-mode
dotnet build ModernFTP.sln
dotnet test ModernFTP.sln
```

The Windows app builds on macOS and Linux too, but only runs on Windows.

## Run the console host

```sh
# create a password hash for a user (reads the password from stdin)
dotnet run --project src/ModernFTP.Host.Console -- hash-password

# check and run a config
dotnet run --project src/ModernFTP.Host.Console -- check-config --config ./config.json
dotnet run --project src/ModernFTP.Host.Console -- serve --config ./config.json
```

A minimal `config.json`:

```json
{
  "port": 2121,
  "passivePortMin": 50000,
  "passivePortMax": 50100,
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

Without `--config` the server looks in `%AppData%\ModernFTP\config.json` on Windows and `~/.config/modernftp/config.json` elsewhere. Put an empty file named `portable` next to the exe to keep the config beside the exe instead. Anonymous access is off unless you set `allowAnonymous` and add a user named `anonymous`. FTPS (explicit `AUTH TLS`) is on by default with a self signed certificate created in the config folder on first run.

Press Ctrl+C to stop the server.

## Windows SmartScreen

ModernFTP is not code signed, so the first time you run the exe Windows SmartScreen may show "Windows protected your PC". To run it anyway:

1. Click **More info**.
2. Click **Run anyway**.

Windows remembers the choice for that file. Download builds only from this repository's releases.

## License

PolyForm Noncommercial 1.0.0, see `LICENSE.md`. Personal and noncommercial use is free. For commercial use, contact Parallax Intelligence Partnership, LLC. Credits to the original are in `NOTICE.md`.

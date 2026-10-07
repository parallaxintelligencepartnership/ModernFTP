# ModernFTP: instructions for Claude

ModernFTP is a clean room rewrite of TYPSoft FTP Server 1.10 (Delphi, 2003) for Windows 11. No original code, forms, translations or artwork are reused, ever. The project brief and every decision record live in `claude-knowledge-base/projects/typsoft-ftp-revival/BRIEF.md`; read it before changing scope.

## Stack (decided, do not reopen without new facts)

- C# on .NET 10 LTS, SDK pinned in `global.json` (10.0.401, rollForward latestFeature).
- `src/ModernFTP.Engine`: hand written FTP engine (RFC 959, 2228, 2389, 3659, 4217). No third party FTP library.
- `src/ModernFTP.Config`: JSON config, config folder (`%AppData%\ModernFTP`, `~/.config/modernftp`, or portable beside the exe), certificate.
- `src/ModernFTP.Host.Console`: cross platform console host (`modernftp serve`, `check-config`, `hash-password`).
- `src/ModernFTP.Host.Service`: Windows service host (`modernftp-service.exe`), config in `%ProgramData%\ModernFTP`.
- `src/ModernFTP.App`: WPF shell with the built in Fluent theme (`ThemeMode="System"`), Windows only, `EnableWindowsTargeting` so it compiles everywhere.
- License: PolyForm Noncommercial 1.0.0. Copyright holder is always written in full: "Parallax Intelligence Partnership, LLC".
- Repos: Gitea `ParallaxIntelligence/ModernFTP` is origin (Drone, Linux); GitHub `parallaxintelligencepartnership/ModernFTP` is the public mirror (Windows runner). `origin` has both push URLs, so `git push origin main` updates both.

## Rules

- US English everywhere. No em dashes, and no hyphen used as a dash, in any UI string, log line, reply text or doc prose.
- No secrets in the repo: no passwords, tokens, keys or PFX files. Test credentials are generated at run time.
- `ModernFTP.Engine` and `ModernFTP.Config` must stay cross platform (no Windows only APIs, no WPF references). Windows specifics belong in the App or a future service host.
- No SFTP (different protocol, different product). No code signing (Matt's standing decision: ship unsigned, document SmartScreen).
- Every NuGet package is pinned to an exact version, `packages.lock.json` files are committed, CI restores with `--locked-mode`.
- `TreatWarningsAsErrors` is on; fix warnings, do not suppress them without a comment explaining why.
- Every client supplied path goes through `VirtualPath.TryResolve`; every handler checks `Permissions(path)` and replies 550 when denied. New handlers must be exception safe.
- Every historical CVE pattern keeps its named regression test in `tests/ModernFTP.Engine.Tests/CveRegressionTests.cs`.
- Tests run with `dotnet test`. The conformance suite needs `curl` on the PATH and skips with a message when it is missing.
- Commits are atomic and carry no Co-Authored-By trailers.
- Phase 3 (Windows fit and packaging) is developed on the `p3` branch and merged to `main` only when Matt says so. Releases are zip files built by `.github/workflows/release.yml`: no installer, no code signing (Matt's decision).

# Changelog

## 0.1.0

- FTP engine written from scratch: RFC 959 with the 2228, 2389, 3659 and 4217 extensions, passive and active mode, explicit FTPS, path jail, per user and per directory permissions.
- Safe defaults: anonymous access off, PBKDF2 password hashes, per IP and total connection limits, idle timeout, persistent IP bans.
- Regression tests for each known TYPSoft FTP Server exploit, and curl driven conformance tests.
- Windows app (WPF, Fluent theme): tray icon, live log, session list, setup and user windows, start with Windows, light and dark themes.
- Console host: `serve`, `check-config`, `hash-password`, `import-typsoft`.
- Importer for TYPSoft FTP Server `config.ini` and `users.ini` (passwords must be set again).
- Windows service host (`modernftp-service.exe`) with `modernftp-cli service install`, `uninstall`, `start`, `stop` and `status`.
- Windows Firewall rules from the app (Setup window button) or `modernftp-cli firewall add` and `remove`.
- About window "Check for updates" button, which only contacts GitHub when clicked.
- Two zip downloads: framework dependent and portable (self contained). No installer, not code signed.

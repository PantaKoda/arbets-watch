# Changelog

## 0.1.0 — unreleased

First version: a Windows tray app that keeps a local copy of the ads currently published on Platsbanken and shows them filtered by place and worktime.

- Downloads the full JobStream snapshot once (about 450 MB, a minute or two), then polls the change stream every 5 minutes (1–60, configurable), with a manual refresh (F5) that shares the service's one-request-per-minute limit.
- Filters: All of Sweden, whole län, single kommuner (any combination), and Heltid / Deltid / Ej angiven. Filtering is local and works offline.
- Newly detected ads are marked and glow once; opening an ad on Platsbanken marks it read; "Mark these as read" clears the current results only.
- Survives restarts, sleep, offline periods and failed downloads without losing the list or read state; long gaps and a weekly reconciliation use a fresh snapshot.
- Tray icon (Show, Refresh, Pause, Settings, Quit), single instance, remembered window position, light/dark theme, optional transparency and always-on-top.
- Portable self-contained ZIP for Windows x64; no installer, not code-signed.

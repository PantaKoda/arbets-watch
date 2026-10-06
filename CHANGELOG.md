# Changelog

## [Unreleased]

- Accent colors, as in Repo Watch: Settings → Appearance → Accent offers Station cyan (default), Nebula violet, Ion blue and Plasma magenta. The accent colors the frame, highlights and selected chips; status colors never change.

## [0.1.0] - 2026-10-06

First version: a Windows tray app that keeps a local copy of the ads currently published on Platsbanken and shows them filtered by place and worktime.

- Downloads the full JobStream snapshot once (about 450 MB, a minute or two), then polls the change stream every 5 minutes (1–60, configurable), with a manual refresh (F5) that shares the service's one-request-per-minute limit.
- Filters: All of Sweden, whole län, single kommuner (any combination), and Heltid / Deltid / Ej angiven. Filtering is local and works offline.
- Newly detected ads are marked and glow once; opening an ad on Platsbanken marks it read; "Mark these as read" clears the current results only.
- Survives restarts, offline periods and failed downloads without losing the list or read state; long gaps and a weekly reconciliation use a fresh snapshot. After the computer sleeps it catches up on the next check (detected from a missed timer; not yet verified with a real sleep).
- Tray icon (Show, Refresh, Pause, Settings, Quit; menu not yet verified by automation), single instance, remembered window position (also on a second display), light/dark theme, optional transparency and always-on-top.
- In-app updates: a daily check of this repository's GitHub releases; an UPDATE button shows what changed and installs the new version after checking its published SHA-256, keeping your saved ads, read state and settings. The previous version stays next to the app folder for a manual rollback.
- Portable self-contained ZIP for Windows x64; no installer, not code-signed.

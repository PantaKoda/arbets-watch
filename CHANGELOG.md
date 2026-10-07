# Changelog

## [0.1.3] - 2026-10-07

- Search: a search box under the filters (Ctrl+F) finds ads by words in the title or the description. It searches only the ads that pass your places and worktime, matches parts of words ("utvecklare" also finds "Systemutvecklare"), requires every word, and takes "quoted phrases". Esc or × clears it. Search only narrows the list; it never changes what counts as new.
- To search descriptions, ArbetsWatch now keeps each current ad's description text locally. After updating, the next refresh downloads the full list once more (about 450 MB) to fetch them; titles are searchable straight away. The database grows accordingly (see the README).

## [0.1.2] - 2026-10-07

- Ad details: a new button next to "Open on Platsbanken" in each row (or Ctrl+D) opens a window with the ad organised into tables: how to apply (the employer's application link or e-mail, reference and instructions when the ad has them), the job (employer, worktime, employment, salary, address, deadline with days left, experience, driving licence), qualifications marked Required or Merit, contacts, and the full description. Details are fetched when you open them and never stored; viewing them marks the ad read.

## [0.1.1] - 2026-10-07

- Accent colors, as in Repo Watch: Settings → Appearance → Accent offers Station cyan (default), Nebula violet, Ion blue and Plasma magenta. The accent colors the frame, highlights and selected chips; status colors never change.
- Fixed: a database created by a pre-release build could fail every refresh ("NOT NULL constraint failed: ad_state.first_seen_utc"). It is now migrated (schema 2) on start, keeping all saved ads and read state.

## [0.1.0] - 2026-10-06

First version: a Windows tray app that keeps a local copy of the ads currently published on Platsbanken and shows them filtered by place and worktime.

- Downloads the full JobStream snapshot once (about 450 MB, a minute or two), then polls the change stream every 5 minutes (1–60, configurable), with a manual refresh (F5) that shares the service's one-request-per-minute limit.
- Filters: All of Sweden, whole län, single kommuner (any combination), and Heltid / Deltid / Ej angiven. Filtering is local and works offline.
- Newly detected ads are marked and glow once; opening an ad on Platsbanken marks it read; "Mark these as read" clears the current results only.
- Survives restarts, offline periods and failed downloads without losing the list or read state; long gaps and a weekly reconciliation use a fresh snapshot. After the computer sleeps it catches up on the next check (detected from a missed timer; not yet verified with a real sleep).
- Tray icon (Show, Refresh, Pause, Settings, Quit; menu not yet verified by automation), single instance, remembered window position (also on a second display), light/dark theme, optional transparency and always-on-top.
- In-app updates: a daily check of this repository's GitHub releases; an UPDATE button shows what changed and installs the new version after checking its published SHA-256, keeping your saved ads, read state and settings. The previous version stays next to the app folder for a manual rollback.
- Portable self-contained ZIP for Windows x64; no installer, not code-signed.

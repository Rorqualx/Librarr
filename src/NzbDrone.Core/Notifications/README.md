# Notifications/ — notification providers

User-configurable outbound notification channels. Each subfolder is one
service; all derive from `NotificationBase`.

## Implementations

- `Apprise/` — meta-service that fans out to many providers.
- `CustomScript/` — runs a shell script on events.
- `Discord/` — webhook to a Discord channel.
- `Email/` — SMTP (uses `Mailkit 3.6.0`).
- `Gotify/`, `Ntfy/`, `Pushover/`, `Pushbullet/`, `PushBullet/`,
  `Boxcar/` — push services.
- `Plex/` — Plex Media Server library refresh.
- `Slack/` — webhook to Slack.
- `Telegram/` — Telegram bot.
- `Webhook/` — generic JSON POST.
- `Synology/` — DSM notifications.

## Lifecycle hooks

`NotificationBase` exposes `OnGrab`, `OnReleaseImport`, `OnDownloadFailure`,
`OnImportFailure`, `OnUpgrade`, `OnRename`, `OnHealthIssue`, `OnBookDelete`,
`OnAuthorDelete`. Provider implementations override only the methods that
make sense for their service.

## Wiring

- `NotificationService` (~465 LoC) — coordinates calls to all enabled
  providers.
- `NotificationStatusService` — health tracking.
- `Definitions/` — per-provider `*Definition` and settings POCOs.
- Provider auto-discovery happens via `ThingiProvider/ProviderFactory`.

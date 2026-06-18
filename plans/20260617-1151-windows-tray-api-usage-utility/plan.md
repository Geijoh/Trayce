# Windows Tray API Usage Utility Plan

## Summary

Build a lightweight Windows desktop utility named Trayce that shows API usage from the notification area. Each tracked API gets its own native tray icon. The icon shows a tiny rendered status value; the tooltip and click flyout show readable calls, tokens, quota, limits, and reset time.

Hard constraint: Windows notification area entries are icons. They support tooltip text and flyouts, but Windows does not provide a reliable always-visible arbitrary text label beside each icon. Treat "icon+text" as icon-rendered short text plus native tooltip/flyout text.

## Recommendation

Use a C# .NET desktop app with direct Win32 `Shell_NotifyIcon` interop for tray icons.

Why:

- Native Windows surface, no Electron.
- `Shell_NotifyIcon` supports add, modify, and delete for notification area icons.
- Multiple icons are supported by assigning each icon a different ID or persisted GUID.
- GUID-backed icons give Windows a stable identity for user visibility preferences.
- Keeps the UI small: tray icons, context menu, compact flyout, settings window.

Skip for MVP:

- Background Windows service.
- Database.
- Plugin marketplace.
- Always-on dashboard window.
- Cross-platform support.

## Product Shape

MVP behavior:

- User configures one or more APIs.
- App starts minimized to tray.
- One tray icon appears per configured API.
- Each icon updates on a timer.
- Tooltip shows the latest snapshot.
- Left click opens a compact API flyout.
- Right click opens a context menu.
- App caches last known values so startup has immediate status.
- App handles network/API failure without losing the last known values.

Tray entry display:

- Icon badge text: shortest useful value, such as `42%`, `8K`, `1.2M`, or `!`.
- Icon color:
  - green: normal
  - amber: nearing limit
  - red: over limit or blocked
  - gray: stale/unknown
- Tooltip format:
  - `OpenAI: 1,284 calls | 3.8M tokens | 42% quota | resets 2026-07-01`

Flyout:

- API name and provider.
- Calls used and limit.
- Tokens used and limit.
- Quota spend/usage percentage.
- Rate-limit status if known.
- Last refresh time.
- Refresh now button.
- Open settings button.

Context menu:

- Refresh now.
- Open details.
- Hide this API.
- Settings.
- Quit Trayce.

## Architecture

Process:

- Single user-mode desktop process.
- Single-instance guard with a named mutex.
- Hidden native window receives tray callbacks.
- No service unless later required for multi-user or pre-login behavior.

Core modules:

- `AppHost`
  - Owns startup, shutdown, mutex, config load, timers.
- `ApiConfigStore`
  - Reads/writes `%AppData%\Trayce\apis.json`.
- `SecretStore`
  - Stores API tokens in Windows Credential Manager or DPAPI-protected data.
- `UsagePoller`
  - Uses `PeriodicTimer`.
  - Polls configured APIs.
  - Applies per-provider timeouts.
- `UsageProvider`
  - Minimal provider abstraction.
  - Add providers one at a time.
- `UsageSnapshotCache`
  - Writes last known snapshot to `%LocalAppData%\Trayce\state.json`.
- `TrayIconManager`
  - Creates one notification icon per API.
  - Updates icons only when snapshot/status changes.
  - Deletes icons when APIs are removed.
- `IconRenderer`
  - Renders small text/status into `.ico`/`HICON` at 16x16 and 32x32.
- `ApiFlyoutWindow`
  - Small native window near the clicked icon.
- `SettingsWindow`
  - Add/edit/remove APIs.

Data model:

```csharp
record ApiConfig(
    string Id,
    string DisplayName,
    string Provider,
    TimeSpan PollInterval,
    long? CallLimit,
    long? TokenLimit,
    decimal? QuotaLimit
);

record UsageSnapshot(
    string ApiId,
    DateTimeOffset ObservedAt,
    long? CallsUsed,
    long? TokensUsed,
    decimal? QuotaUsed,
    DateTimeOffset? ResetsAt,
    UsageState State,
    string? Message
);

enum UsageState { Unknown, Normal, Warning, Critical, Error }
```

Provider contract:

```csharp
interface UsageProvider
{
    Task<UsageSnapshot> GetUsageAsync(ApiConfig config, CancellationToken ct);
}
```

This interface is justified because multiple APIs/providers are a core requirement.

## Native Windows Details

Use `Shell_NotifyIcon` directly rather than only `System.Windows.Forms.NotifyIcon` if stable per-API GUIDs and exact flyout positioning matter.

Implementation notes:

- Persist one GUID per API in config.
- Use `NOTIFYICON_VERSION_4`.
- Use `NIF_ICON`, `NIF_TIP`, `NIF_MESSAGE`, and `NIF_GUID`.
- Use `NIM_ADD` on startup.
- Use `NIM_MODIFY` for icon/tooltip updates.
- Use `NIM_DELETE` on API removal/shutdown.
- Use `Shell_NotifyIconGetRect` for flyout placement.
- Provide high-DPI icon sizes.

Fallback:

- If direct Win32 interop slows the MVP, start with `System.Windows.Forms.NotifyIcon`.
- Move to direct `Shell_NotifyIcon` when visibility persistence or flyout positioning becomes painful.

## Usage Data Strategy

Do not build a generic scraping layer.

Use provider adapters:

- Official usage endpoint when the provider has one.
- Local request instrumentation when the app/user controls the outgoing calls.
- Manual quota limits from settings when provider only returns usage, not limits.

MVP provider order:

1. Mock/local JSON provider for UI development.
2. One real provider.
3. Add more providers only after the tray behavior is solid.

## Settings

Store non-secrets in:

- `%AppData%\Trayce\apis.json`

Example:

```json
{
  "apis": [
    {
      "id": "openai-main",
      "displayName": "OpenAI",
      "provider": "openai",
      "pollIntervalSeconds": 300,
      "callLimit": null,
      "tokenLimit": 10000000,
      "quotaLimit": 250.00,
      "trayGuid": "PUT-STABLE-GUID-HERE"
    }
  ]
}
```

Store secrets outside this file.

## Error Handling

Rules:

- Never crash because one API fails.
- Keep last good snapshot.
- Mark stale data after 2 missed refreshes.
- Show `!` badge for auth/rate-limit errors.
- Back off after repeated failures.
- Limit notifications to critical events.

## Security

Minimum:

- No API keys in plain JSON.
- Secrets stored with Windows Credential Manager or DPAPI.
- Redact secrets from logs.
- Timeout all network calls.
- Do not send provider data anywhere except configured provider endpoints.

## Tests

Small checks only:

- `IconRenderer` produces non-empty icons for normal/warning/error/stale.
- `TrayIconManager` adds, updates, and removes N icons from fake snapshots.
- `UsagePoller` keeps last good snapshot after provider failure.
- Config load rejects duplicate API IDs.

No UI automation for MVP unless tray behavior proves flaky.

## Implementation Phases

### Phase 1 - Native Tray Spike

Goal: prove multiple native tray icons.

Tasks:

- Create .NET desktop app.
- Create hidden window for tray callbacks.
- Add two hard-coded tray icons.
- Update tooltip and icon text every few seconds.
- Delete icons cleanly on exit.

Done when:

- Two separate tray entries appear.
- Each has different icon and tooltip.
- Left/right click events identify the correct API.

### Phase 2 - Local Config And Cache

Goal: make APIs configurable without real provider work.

Tasks:

- Add `%AppData%\Trayce\apis.json`.
- Add `%LocalAppData%\Trayce\state.json`.
- Load API list on startup.
- Create one icon per config item.
- Use mock snapshots.

Done when:

- Adding/removing config entries changes tray icons after restart.
- Last snapshot appears immediately on startup.

### Phase 3 - Flyout And Menu

Goal: make each icon useful.

Tasks:

- Add compact flyout window.
- Position near clicked icon.
- Add right-click context menu.
- Wire refresh/details/settings/quit commands.

Done when:

- Each API opens its own details.
- Menu actions target the correct API.

### Phase 4 - First Real Provider

Goal: prove real usage data.

Tasks:

- Add one provider adapter.
- Store its token securely.
- Poll on interval.
- Map provider response to `UsageSnapshot`.
- Handle auth, network, and rate-limit errors.

Done when:

- Real calls/tokens/quota update the tray icon.
- Failure keeps last good data and marks stale/error.

### Phase 5 - Settings Window And Packaging

Goal: usable local app.

Tasks:

- Add native settings window.
- Add API add/edit/remove.
- Add startup toggle.
- Package as a normal Windows desktop app.

Done when:

- Non-technical user can add an API.
- App can start with Windows.
- Uninstall leaves no running process.

## Open Decisions

- First real API provider.
- Whether visible tray text must be literal text beside the icon. If yes, Windows notification area is the wrong surface.
- Whether each API must be independently hideable from Trayce settings or only through Windows tray overflow settings.
- Whether usage should come from provider billing APIs, local request instrumentation, or both.

## Sources

- Microsoft `Shell_NotifyIcon`: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona
- Microsoft `NOTIFYICONDATA`: https://learn.microsoft.com/en-us/windows/win32/api/shellapi/ns-shellapi-notifyicondataw
- Microsoft notification area guidance: https://learn.microsoft.com/en-us/windows/win32/shell/notification-area
- Microsoft WinForms `NotifyIcon`: https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.notifyicon
- Microsoft WinUI 3 overview: https://learn.microsoft.com/en-us/windows/apps/winui/winui3/

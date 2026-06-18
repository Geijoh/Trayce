# Trayce

Lightweight Windows tray utility for watching API usage at a glance.

## Run

```powershell
dotnet run
```

On first run, Trayce creates:

```text
%APPDATA%\Trayce\apis.json
%LOCALAPPDATA%\Trayce\state.json
```

Each configured API gets one tray icon. The icon is a rounded brand-color badge with the logo/text mark and up to two tiny health bars beneath it; a status dot appears when the last refresh is stale or failed.

- Hover for the summary tooltip.
- Left-click for a compact Windows 11–style details flyout with stacked limit bars and a status banner.
- Right-click for a custom menu: refresh, details, settings, config JSON, reload, startup toggle, or quit.
- Live usage is cached, so startup can show the last known values before the first refresh returns.
- Settings can browse for a logo and edit stacked limit windows.
- UI runs per-monitor DPI aware and scales custom-drawn bars/icons with Windows DPI.

## Theme

Trayce follows the Windows light/dark setting by default and updates live when you change it. The settings window has a **Match system** toggle plus a **Light/Dark** switch (the switch is disabled while *Match system* is on). The choice is saved as `theme` (`system`, `light`, or `dark`) in `apis.json`.

## Config

Static usage:

```json
{
  "apis": [
    {
      "id": "openai",
      "displayName": "OpenAI",
      "logoText": "AI",
      "brandColor": "#111827",
      "pollSeconds": 300,
      "usage": {
        "calls": 1284,
        "tokens": 3800000,
        "quota": 105.42,
        "quotaLimit": 250.00,
        "windows": [
          {
            "label": "5h",
            "metric": "tokens",
            "used": 180000,
            "limit": 500000,
            "resetsAt": "2026-06-17T16:00:00-07:00"
          },
          {
            "label": "7d",
            "metric": "tokens",
            "used": 3800000,
            "limit": 10000000,
            "resetsAt": "2026-06-21T00:00:00-07:00"
          }
        ]
      }
    }
  ]
}
```

Logo options:

- `logoPath`: path to a small PNG/JPG/ICO. Relative paths resolve from `%APPDATA%\Trayce`.
- `logoText`: short fallback mark, such as `AI` or `GH`.
- `brandColor`: hex color used behind the mark.

Live usage endpoint:

```json
{
  "id": "internal-meter",
  "displayName": "Meter",
  "logoText": "M",
  "brandColor": "#2563EB",
  "sourceUrl": "https://localhost:5001/usage.json",
  "pollSeconds": 60
}
```

The endpoint should return the same shape as `usage`. Use `windows` for service-specific limits such as 5-hour, daily, weekly, or monthly quotas.

## Check

```powershell
dotnet run -- --self-test
```

Render a details preview:

```powershell
dotnet run -- --render-preview .\trayce-preview.png
```

## Publish

```powershell
dotnet publish -c Release
```

Output:

```text
bin\Release\net8.0-windows\win-x64\publish\Trayce.exe
```

The project publishes as a compressed, self-contained single-file Windows x64 app by default.

Create a zip:

```powershell
powershell -ExecutionPolicy Bypass -File .\scripts\package.ps1
```

Output:

```text
dist\Trayce-win-x64.zip
```

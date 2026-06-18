# Privacy Policy

Effective date: June 18, 2026

Trayce is a native Windows tray utility that watches API usage locally on your PC.

## Data Collection

Trayce does not collect, transmit, sell, rent, or share personal data.

The app does not include analytics, telemetry, advertising, tracking pixels, or crash-reporting services.

## Local Configuration

Trayce stores API entries and app preferences in:

```text
%APPDATA%\Trayce\apis.json
```

These settings can include API names, provider labels, logo paths, logo initials, brand colors, source URLs, poll cadence, usage limits, theme preference, tray style, and any API key text you enter.

Trayce stores cached usage state in:

```text
%LOCALAPPDATA%\Trayce\state.json
```

This cache lets Trayce show the last known usage values before the next refresh completes.

## Local Files

Trayce reads logo files only when you choose a custom logo in settings. Imported logos are copied under `%APPDATA%\Trayce`.

Trayce does not upload logo files, config files, or cached usage state.

## Network Access

Trayce makes network requests only to the `sourceUrl` values you configure for API usage refreshes.

Usage refreshes are HTTP `GET` requests. Trayce expects the endpoint to return JSON in the same shape as a local `usage` object.

Trayce may open your default browser when you click links such as GitHub or Privacy Policy.

## Startup Setting

If you enable **Start with Windows**, Trayce writes a per-user startup entry under:

```text
HKCU\Software\Microsoft\Windows\CurrentVersion\Run
```

Disabling the setting removes that entry.

## Contact

For privacy questions, open an issue at:

https://github.com/Geijoh/Trayce/issues

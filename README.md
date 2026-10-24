# MacPartsFinder

The Mac edition of [WinPartsFinder](https://github.com/Ian-Johnston/WinPartsFinder) by Ian Johnston.

MacPartsFinder searches several electronic component suppliers at once - Mouser, DigiKey, element14 (Farnell, CPC
and Newark) and TME through their official APIs, plus website links for RS, Rapid, LCSC, AliExpress and any you
add - and puts stock, prices, minimum order and lifecycle status side by side in one grid.

## Download

Get the latest `.dmg` from the **Releases** page:

- **MacPartsFinder-mac-arm64.dmg** - Apple Silicon Macs (M1 and later). Apple menu > About This Mac says "Chip: Apple M…".
- **MacPartsFinder-mac-intel.dmg** - older Intel Macs ("Processor: … Intel …").

Open the `.dmg` and drag **MacPartsFinder** into **Applications**.

## First launch

MacPartsFinder isn't signed by Apple yet, so the first time you open it macOS says it can't check it for malicious
software and won't open it. To allow it, once:

1. Double-click MacPartsFinder in Applications and click **Done** (or **OK**) on the warning.
2. Open **System Settings > Privacy & Security** and scroll down to Security.
3. Next to "MacPartsFinder was blocked…", click **Open Anyway**, then confirm with your password or Touch ID.

After that it opens normally. (On macOS 15 and later, right-click > Open no longer does this.)

If macOS says the app **is damaged** instead, open **Terminal** and run this, then open it again:

```bash
xattr -dr com.apple.quarantine /Applications/MacPartsFinder.app
```

## API keys

Each user needs their own free API keys - click **Settings…**, then **API Help** for step-by-step instructions.
Keys are kept in your Mac's Keychain (they show as MacPartsFinder in Keychain Access), never in the app or its
settings file. If macOS asks whether MacPartsFinder may use your Keychain, click **Always Allow**; a new test build
may ask once more.

Settings are saved in `~/Library/Application Support/MacPartsFinder/settings.json`.

## Building

C# with [Avalonia UI](https://avaloniaui.net), .NET 10. `dotnet build` builds and runs it on Windows for testing.
The Mac downloads are built by GitHub Actions (`.github/workflows/release.yml`): push a tag such as `v0.1.0` for a
pre-release, or run the workflow by hand for a test build.

## Licence

Free for personal, non-commercial use - see About in the app for the full licence and disclaimer.
© 2026 Ian Johnston.

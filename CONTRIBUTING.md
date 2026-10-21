# Contributing

Bug reports and pull requests in English or Russian are welcome.

## Development

Use Windows x64, .NET SDK 8 or later, and the Windows SDK / WinUI development tools. Open `AndroidDesk.sln` in Visual Studio or run:

```powershell
.\build.ps1
dotnet run --project .\AndroidDesk.Smoke -- --language
dotnet run --project .\AndroidDesk.Smoke -- --wifi-validation
```

The x64 `scrcpy/` tools are versioned with the project. Use `build.ps1 -Architecture x86` or `-Architecture arm64` for other targets; matching upstream bundles are downloaded and SHA-256 verified. Avoid replacing them as part of unrelated changes: the clipboard control protocol depends on the bundled scrcpy-server version.

## Pull requests

- Keep changes focused and describe the resulting behavior.
- Preserve the compact phone viewport and responsive quick access controls.
- Add English translations to `AndroidDesk/Strings.en.json` when adding app-owned Russian labels.
- Keep user command text, device output and clipboard contents out of logs.
- Build the app and run checks appropriate to the change. Changes to mirroring, recording or input need a real Android device; describe what was and was not tested.
- Include screenshots for visual changes, with personal information removed.
- Do not commit `bin/`, `obj/`, `dist/`, recordings, credentials or local settings.

Contributions to the application are provided under the [MIT license](LICENSE). Third-party components retain their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

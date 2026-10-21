# scrcpy GUI

**English** · [Русский](README.ru.md)

A compact Windows app for controlling an Android phone with **C# / WinUI 3**, **ADB** and the existing **scrcpy** engine. The app window is called **Android Desk**.

The phone screen fills the window, with a narrow quick access toolbar on the right. Settings, device selection and saved commands open in separate windows.

## Features

- Embedded phone screen with mouse and keyboard input.
- Automatic device detection and selection of USB or Wi-Fi devices.
- Wireless debugging pairing by code on Android 11+ and manual IP:port connections.
- Back, Home, Recents, volume, power and screenshot controls.
- MP4 or MKV recording with H.264 video and optional AAC device audio.
- Resolution limits of 720, 960, 1280, 1600, 1920 and 2560 pixels, or original resolution; frame rate and bit rate controls.
- Shared text clipboard in both directions.
- Searchable saved ADB commands, with pinned shortcuts on the side toolbar.
- Russian and English UI, system language detection and instant manual switching.
- A fit button that adjusts window width to the phone while keeping the current height.

## Requirements

- Windows 10 version 2004 or later, x64. The interface is designed for Windows 11.
- An Android phone with debugging enabled and the computer authorized on the phone.
- Device audio recording and code-based wireless debugging require Android 11+.
- To build: .NET SDK 8 or later and the Windows SDK / WinUI development tools. NuGet access is needed on the first build. Visual Studio with Windows application development tools is recommended.

The published app includes .NET and Windows App SDK; a separate runtime installation, MSIX package or administrator access is not required.

## Build and run

```powershell
git clone git@github.com:SPomodor/scrcpy-gui.git
cd scrcpy-gui
.\build.ps1 -Publish
.\run.ps1
```

Or build directly:

```powershell
dotnet publish .\AndroidDesk\AndroidDesk.csproj -c Release -p:Platform=x64 -o .\dist\AndroidDesk
& '.\dist\AndroidDesk\AndroidDesk.exe'
```

After publishing, you can also double-click `Android Desk.cmd`. To move the app to another computer, copy the **entire** `dist/AndroidDesk` folder. Close the running app before publishing into the same folder again.

The repository includes the supplied **scrcpy 5.0.1 Windows x64 bundle** in `scrcpy/`. Its tools are copied into the app output. The clipboard bridge uses the matching scrcpy-server protocol, so do not replace just one binary with an unrelated version.

The [Windows build workflow](.github/workflows/build.yml) builds the app, runs checks that do not require a phone and uploads a portable build as a GitHub Actions artifact. Artifacts require a completed workflow run; no installer is generated.

## Connect a phone

### USB

Enable USB debugging, connect the phone and approve the computer on the phone. The app refreshes the device list every two seconds. Use **Select device** to choose between multiple phones or transports.

### Wi-Fi

1. Connect the phone and computer to the same network.
2. On Android 11+, enable **Developer options → Wireless debugging**.
3. In the app, open **Select device → Wi-Fi**.
4. For a first connection, expand **First connection · pairing**. On the phone, choose **Pair device with pairing code**, then enter that dialog's IP:port and six-digit code in the app.
5. Enter the connection address from the main Wireless debugging screen and click **Add device**. **The pairing port and connection port are different.**
6. Select the Wi-Fi device in the device list and click **Connect**. The USB cable can then be removed.

The app discovers addresses through ADB mDNS; manual entry works when discovery is unavailable. The port may change after restarting wireless debugging or switching networks. Existing ADB TCP/IP endpoints such as `IP:5555` can also be entered, but the app does not enable that mode automatically.

## Recording, input and clipboard

Use the record button on the toolbar to start or stop recording. In **⋯ Settings**, choose MP4/MKV and whether to include device audio. Regular mirroring is silent; recording can capture the phone's output without playing it on the PC. The microphone is not recorded. On Android 11, unlock the phone before starting audio capture.

Recording is configured when scrcpy starts. During recording and stream-setting transitions, the old stream stays visible until the replacement is ready. This briefly uses two scrcpy processes and depends on the phone's encoder capabilities. If the replacement fails, the existing session is kept. Wait for recording to finish before disconnecting the phone.

Click the phone screen before typing. Physical keyboard input (UHID) is enabled by default; compatible SDK input is available in Settings. For Russian typing, select a Russian **physical keyboard layout in Android**; Alt+K opens its layout settings. Ctrl+V pastes PC text. The shared clipboard synchronizes **text only**, not files or images.

## Saved ADB commands

Open the terminal button on the side toolbar. Enter a name and a command, then save it. For example:

```text
adb shell getprop ro.product.model
```

The `adb` / `adb.exe` prefix is optional. The selected phone is added automatically; do not prefix commands with `-s`, `-d` or `-e`. Search covers names and command text. Enable **Pin to the quick access toolbar** and save to add a one-click shortcut. Hover over a shortcut for its full name and command; scroll the shortcut area if many commands are pinned.

Output and exit codes are shown in the command window. Running a pinned command does not open that window; its last result is available there afterward. Commands use the phone selected at launch and do not block the other phone controls. A command can be stopped in the editor and has a 60-second timeout. Stopping the ADB client may leave its Android process running. This is a text command runner, not an interactive terminal.

Arguments are passed directly to ADB, without cmd.exe or PowerShell. Text after `shell` keeps its Android shell quoting. Use double quotes around Windows file paths containing spaces.

## Language and local files

Choose **System default / Русский / English** in **⋯ Settings → Interface language**. System default uses Russian for `ru-*` Windows languages and English otherwise. Switching language applies immediately without restarting the stream. User command names, command text and device output are preserved.

| Data | Location |
| --- | --- |
| Preferences and saved commands | `%LOCALAPPDATA%\AndroidDesk\settings.json`, `commands.json` |
| Diagnostic log | `%LOCALAPPDATA%\AndroidDesk\app.log` |
| Video recordings | Windows Videos folder → `AndroidDesk` |
| PNG screenshots | Windows Pictures folder → `AndroidDesk` |

Logs may include device identifiers. Clipboard text and command output are not written to the log automatically. User data, captures and build output are excluded from Git.

## Known limitations

Embedding uses Win32 `SetParent` to host scrcpy's SDL window; it is not a custom streaming engine. Behavior can depend on SDL, graphics drivers and DPI settings. Use **Separate scrcpy window** in Settings if embedding fails on your computer.

USB usually gives the lowest latency. Wi-Fi performance depends on the network. Android and individual apps may restrict audio capture or protected screen content. Some manufacturers require an additional debugging permission for input control.

## Development and checks

```powershell
.\build.ps1                                      # Debug build
dotnet run --project .\AndroidDesk.Smoke -- --language
dotnet run --project .\AndroidDesk.Smoke -- --wifi-validation
```

Real-device checks, run individually with a ready phone:

```powershell
dotnet run --project .\AndroidDesk.Smoke -- DEVICE_SERIAL
dotnet run --project .\AndroidDesk.Smoke -- DEVICE_SERIAL --clipboard
dotnet run --project .\AndroidDesk.Smoke -- --commands DEVICE_SERIAL
dotnet run --project .\AndroidDesk.Smoke -- --wifi IP:PORT
```

The first check creates a short MP4/AAC recording. The clipboard check restores the original text afterward. Command checks use read-only queries and a short sleep. Wi-Fi integration requires a reachable, already paired endpoint. See [CONTRIBUTING.md](CONTRIBUTING.md) and the historical [verification notes in Russian](VERIFICATION.md).

## License and credits

Application code and documentation: [MIT](LICENSE), copyright © 2026 SPomodor. Bundled tools and dependencies keep their own licenses; see [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Built on [scrcpy](https://github.com/Genymobile/scrcpy), [ADB](https://developer.android.com/tools/adb) and [Windows App SDK](https://github.com/microsoft/WindowsAppSDK). This is an independent GUI project.

by [SPomodor](https://github.com/SPomodor)

# Security

Please report security issues privately using [GitHub private vulnerability reporting](https://github.com/SPomodor/scrcpy-gui/security/advisories/new) when available. If that option is unavailable, contact [SPomodor](https://github.com/SPomodor) privately before publishing sensitive details.

Include the affected commit or version, reproduction steps and the likely impact. Do not post ADB private keys, pairing codes, clipboard contents or unredacted device logs in public issues.

ADB commands run with the permissions granted by Android debugging. Saved and pinned commands execute exactly what the user enters. The application does not sandbox Android commands or change the phone's debugging permissions automatically. It launches the bundled ADB executable directly, without a Windows command shell.

Security fixes target the current `main` branch. There is no promised support window for older builds.

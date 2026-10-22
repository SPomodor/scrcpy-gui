# Third-party software

The [MIT license](LICENSE) covers the scrcpy Desk application code and documentation by SPomodor. It does not replace the licenses of bundled tools, libraries or Microsoft runtime components.

## scrcpy bundle

The repository contains the supplied Windows x64 scrcpy 5.0.1 bundle in `scrcpy/`. Release builds download the official matching x64, x86 or ARM64 bundle using pinned SHA-256 checksums in `scripts/scrcpy-bundles.json`. The official ARM64 bundle includes x86 ADB and its matching x86 DLLs; Windows on Arm executes that external tool through OS emulation. These components are used as external tools and shared libraries; the application does not implement a replacement scrcpy engine. Their original license files and notices must accompany redistributed builds.

| Component | Version | License / notice | Upstream source |
| --- | --- | --- | --- |
| scrcpy client and server | 5.0.1 | [Apache-2.0](scrcpy/LICENSE.txt); © Genymobile and Romain Vimont | [Source and build instructions](https://github.com/Genymobile/scrcpy/tree/v5.0.1) |
| Android Debug Bridge (ADB), AdbWinApi, AdbWinUsbApi | Platform Tools 37.0.1 | [ADB notice](licenses/ADB-NOTICE.txt), including Apache-2.0 | [Android Open Source Project](https://android.googlesource.com/platform/packages/modules/adb/) |
| SDL | 3.4.18 | [zlib license](licenses/SDL.txt) | [SDL source](https://github.com/libsdl-org/SDL/tree/release-3.4.18) |
| FFmpeg libraries: avcodec, avformat, avutil, swresample | 9.0.2 | [LGPL-2.1 text](licenses/LGPL-2.1.txt); upstream LGPL-2.1-or-later terms | [FFmpeg source archive](https://ffmpeg.org/releases/ffmpeg-9.0.2.tar.xz) |
| libusb | 1.0.30 | [LGPL-2.1-or-later](licenses/LGPL-2.1.txt) | [libusb source](https://github.com/libusb/libusb/tree/v1.0.30) |
| dav1d, used by the FFmpeg build | 1.5.4 | [BSD-2-Clause notice](licenses/dav1d.txt) | [dav1d source archive](https://downloads.videolan.org/pub/videolan/dav1d/1.5.4/dav1d-1.5.4.tar.xz) |
| zlib, used by the FFmpeg build | See upstream build/toolchain | [zlib license](licenses/zlib.txt) | [zlib source](https://github.com/madler/zlib) |

Versions and build configuration are documented in the [scrcpy 5.0.1 dependency scripts](https://github.com/Genymobile/scrcpy/tree/v5.0.1/app/deps). The supplied FFmpeg configuration enables shared libraries and does not enable GPL or nonfree components. Keep the FFmpeg and libusb DLLs replaceable when redistributing the app. Source and build scripts for those libraries are linked above; their licenses continue to apply independently of the GUI.

Official scrcpy Windows download: [scrcpy-win64-v5.0.1.zip](https://github.com/Genymobile/scrcpy/releases/download/v5.0.1/scrcpy-win64-v5.0.1.zip). For source builds, follow the upstream instructions and corresponding dependency scripts rather than mixing client/server versions.

## .NET and Windows App SDK

Self-contained publishing includes Microsoft runtime components and NuGet dependencies. They retain their respective licenses and notices:

- [.NET runtime license](licenses/dotnet.txt), [.NET third-party notices](licenses/dotnet-THIRD-PARTY-NOTICES.txt), and [runtime source](https://github.com/dotnet/runtime).
- [Windows App SDK license](licenses/WindowsAppSDK.txt) and [source](https://github.com/microsoft/WindowsAppSDK). The project references Microsoft.WindowsAppSDK 1.8.260921001; additional package terms are included in the upstream NuGet packages.

The root license, this notice, and `licenses/` are copied into the published app. The original scrcpy license is preserved as `scrcpy/LICENSE.txt` in the published bundle.

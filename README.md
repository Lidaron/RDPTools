# RDP Tools

RDP Tools is a Windows tray utility for Windows App remote sessions hosted by `msrdc.exe` or `Windows365.exe`. It provides windowed pseudo-maximize behavior while preserving MSRDC's native full-screen action:

- Pressing `Win+Up` fills the current monitor's work area without entering full screen.
- Dragging the session window to the top edge for Aero Snap fills the work area without entering full screen.
- Double-clicking the title bar is passed through unchanged, so MSRDC can enter or leave its native full-screen mode.
- While a managed window fills the work area, shell-reserved keyboard chords are redirected to MSRDC, including Windows-key shortcuts, `Alt+Tab`, `Alt+Esc`, `Alt+F4`, and `Ctrl+Esc`.

The utility uses Windows low-level mouse and keyboard hooks in its own process. It does not inject a DLL into MSRDC or modify the client installation.

## Requirements

- Windows 10 or Windows 11, x64 or ARM64
- Windows App or the Microsoft Remote Desktop client
- .NET 10 SDK to build from source

Run RDP Tools at the same integrity level as MSRDC. If MSRDC is elevated, RDP Tools must also be elevated or Windows UIPI can block forwarded keyboard messages.

## Build and run

```powershell
dotnet build RDPTools.csproj -c Release
.\bin\Release\net10.0-windows\RDPTools.exe
```

Only one instance runs per Windows session. Use the tray menu to temporarily disable the hooks, restore all managed windows, or exit. Exiting restores any windows that are still managed.

## Publish a standalone executable

```powershell
dotnet publish RDPTools.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish\win-arm64
dotnet publish RDPTools.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish\win-x64
```

Use the executable under `publish\win-arm64` on ARM64 Windows or `publish\win-x64` on x64 Windows. The published executable does not require a separately installed .NET runtime.

## Keyboard limitations

Forwarded shortcuts are posted to MSRDC's focused window because Windows normally reserves them for the local shell. Whether a particular chord reaches the remote session still depends on the installed MSRDC version and its input handling.

Windows does not allow a normal desktop process to synthesize secure-attention sequences. Use MSRDC's supported `Ctrl+Alt+End` sequence when the remote session needs `Ctrl+Alt+Delete`. OS-secured shortcuts such as `Win+L` can also remain local on some Windows versions.
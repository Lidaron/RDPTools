# RDP Tools

RDP Tools is a Windows tray utility for Windows App remote sessions hosted by `msrdc.exe` or `Windows365.exe`. It provides windowed pseudo-maximize behavior while preserving MSRDC's native full-screen action:

- Pressing `Win+Up` fills the current monitor's work area without entering full screen.
- Dragging the session window to the top edge for Aero Snap fills the work area without entering full screen.
- Double-clicking the title bar is passed through unchanged, so MSRDC can enter or leave its native full-screen mode.
- Windows App connections are configured with `keyboardhook:i:1`, the RDP setting equivalent to MSTSC's **Apply Windows key combinations: On the remote computer**.
- While the remote session has focus, physical OS shortcuts such as `Alt+Tab`, `Win`, `Win+R`, `Ctrl+Esc`, and `Alt+F4` are handled by the remote session itself.

The utility uses Windows low-level mouse and keyboard hooks in its own process. It does not inject a DLL into MSRDC or modify the installed application binaries.

## Requirements

- Windows 10 or Windows 11, x64 or ARM64
- Windows App or the Microsoft Remote Desktop client
- .NET 10 SDK to build from source

Run RDP Tools at the same integrity level as MSRDC so the hooks observe the same desktop input stream.

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

RDP Tools updates Windows App's cached RDP resource and launch payloads under its per-user package data. It adds this standard RDP property:

```text
keyboardhook:i:1
```

Cached payloads can be signed. RDP Tools changes the keyboard policy only when the payload's `signscope` does not include `keyboardhook`; it never changes signed connection, authentication, or gateway fields.

The setting is read when an RDP connection starts. After RDP Tools is installed or updated, disconnect the existing remote session and reconnect it before testing keyboard shortcuts. Merely closing or restoring the session window is not sufficient.

Windows does not allow a normal desktop process to synthesize secure-attention sequences. Use Windows App's supported `Ctrl+Alt+End` sequence when the remote session needs `Ctrl+Alt+Delete`.
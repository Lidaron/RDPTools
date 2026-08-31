# RDP Tools

<p align="center">
	<img src="assets/favicon.png" width="128" height="128" alt="RDP Tools icon: connected local and remote windows">
</p>

## Project overview

RDP Tools is a small Windows tray utility that makes windowed Windows App remote sessions behave more predictably. It targets sessions hosted by `msrdc.exe` or `Windows365.exe` and removes a few points of friction between local window management and remote keyboard input.

The utility keeps native full screen available, but makes ordinary maximize gestures fill the monitor's usable work area instead. It also configures Windows App to send operating-system keyboard shortcuts to the remote computer while the session has focus.

## What it changes

| Interaction | Result |
| --- | --- |
| Press `Win+Up` on a normal session window | Fill the current monitor's work area without entering full screen |
| Drag the session window to the top edge | Use the same work-area maximize behavior without entering native full screen |
| Double-click the title bar | Preserve Windows App's native full-screen action |
| Press `Alt+Tab`, `Win`, `Win+R`, `Ctrl+Esc`, or `Alt+F4` while the session has focus | Handle the shortcut on the remote computer |
| Disable RDP Tools from its tray menu | Restore managed windows and stop intercepting window gestures |

This creates a deliberate distinction: common maximize gestures keep the session windowed, while a title-bar double-click remains an explicit way to enter or leave native full screen.

## Accessibility and focus use cases

Small, consistent interactions can remove repeated effort from a remote-work session. RDP Tools may be useful for people whose access needs make precision, context changes, or unpredictable window behavior more costly.

### Reduced pointer precision and motor fatigue

`Win+Up` offers a keyboard-first way to use the available screen without aiming for a small maximize button. Dragging to the top edge provides a large pointer target for people who find precise clicking or double-click timing difficult because of tremor, limited dexterity, pain, or fatigue.

Because double-clicking remains reserved for full screen, an accidental maximize gesture is less likely to hide the local desktop controls.

### Stable visual context

Work-area maximize leaves the local taskbar visible. That can provide a consistent orientation cue, preserve access to local accessibility tools, and make it easier to check local notifications or the time without leaving the remote session entirely.

The session returns to its previous placement when restored, reducing unexpected layout changes across repeated maximize and restore cycles.

### Fewer focus and context errors

When a remote session looks active, it is easy to expect `Alt+Tab`, the Windows key, or `Win+R` to act remotely. Having those shortcuts unexpectedly affect the local desktop can interrupt concentration, expose unrelated local windows, or make it unclear which computer currently owns the interaction.

RDP Tools applies the same remote-keyboard policy offered by MSTSC. Shortcuts follow the focused remote session, which reduces the mental bookkeeping required to remember separate replacement key combinations.

### Keyboard-first and focus-sensitive workflows

People who avoid frequent pointer movement can maximize the session and navigate remote applications from the keyboard. People with attention-regulation or executive-function needs may benefit from keeping one predictable work surface while still retaining an intentional route back to the local desktop.

These are practical use cases, not a claim that RDP Tools is certified assistive technology. Access needs vary, and low-level input hooks can interact with screen readers, keyboard remappers, voice-control software, or other accessibility tools. Test the combination you rely on and use the tray menu to disable RDP Tools if there is a conflict.

## How it works

RDP Tools runs in the notification area and installs low-level mouse and keyboard hooks in its own process. It does not inject a DLL into Windows App or modify the installed application binaries.

For remote keyboard handling, it adds the standard RDP property below to Windows App's per-user cached resource and launch payloads:

```text
keyboardhook:i:1
```

This is equivalent to selecting **Apply Windows key combinations: On the remote computer** in MSTSC. Cached payloads can be signed, so RDP Tools changes the property only when the payload's `signscope` does not include `keyboardhook`. It does not alter signed connection, authentication, or gateway fields.

The utility does not record keystrokes or provide remote-session credentials. Key state is tracked only in memory for recognizing the window-management gesture.

## Requirements

- Windows 10 or Windows 11
- Windows App or Microsoft Remote Desktop
- An x64 or ARM64 computer
- .NET 10 SDK when building from source

Run RDP Tools at the same integrity level as Windows App so its hooks can observe the same desktop input stream.

## Build and run

```powershell
dotnet build RDPTools.csproj -c Release
.\bin\Release\net10.0-windows\RDPTools.exe
```

Only one instance runs in each Windows session. The tray menu provides these commands:

- **Enabled** temporarily enables or disables gesture handling.
- **Restore managed windows** returns every pseudo-maximized session to its previous placement.
- **Exit** restores managed windows and closes RDP Tools.

Start RDP Tools before opening a new remote connection. If a session was already connected, fully disconnect and reconnect it so Windows App reads the keyboard policy. Closing, minimizing, or restoring the session window is not sufficient.

## Publish a standalone executable

### ARM64 Windows

```powershell
dotnet publish RDPTools.csproj -c Release -r win-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish\win-arm64
```

### x64 Windows

```powershell
dotnet publish RDPTools.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish\win-x64
```

The published executable does not require a separately installed .NET runtime.

## Limitations

- The remote keyboard policy takes effect only when a new RDP connection starts.
- Windows does not allow a normal desktop process to synthesize the secure-attention sequence. Use Windows App's supported `Ctrl+Alt+End` shortcut when the remote computer needs `Ctrl+Alt+Delete`.
- Elevated Windows App sessions require RDP Tools to run at the same integrity level.
- Windows App updates may change its package data layout and require a corresponding RDP Tools update.
- Input hooks or key remapping from other utilities may take precedence or conflict. Disable RDP Tools from the tray when diagnosing an input problem.

## License

RDP Tools is available under the [MIT License](LICENSE).
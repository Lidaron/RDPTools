using System.ComponentModel;
using System.Runtime.InteropServices;

namespace RDPTools;

internal sealed class InputHookService : IDisposable
{
    private const nuint ReplayInputMarker = 0x52445054;

    private readonly MsrdcWindowController _windowController;
    private readonly Control _dispatcher;
    private readonly NativeMethods.HookProc _mouseCallback;
    private readonly NativeMethods.HookProc _keyboardCallback;
    private readonly HashSet<uint> _physicalKeysDown = [];
    private readonly HashSet<uint> _suppressedPhysicalKeys = [];

    private nint _mouseHook;
    private nint _keyboardHook;
    private nint _lastCaptionWindow;
    private MsrdcWindowController.AeroSnapCandidate _aeroSnapCandidate;
    private NativeMethods.Point _lastCaptionPosition;
    private NativeMethods.Point _aeroSnapStartPosition;
    private uint _lastCaptionClickTime;
    private uint _pendingNormalWindowsKey;
    private uint _pendingNormalWindowsScanCode;
    private uint _pendingNormalWindowsFlags;
    private nint _pendingNormalWindowsWindow;
    private bool _enabled = true;

    internal InputHookService(MsrdcWindowController windowController)
    {
        _windowController = windowController;
        _mouseCallback = MouseHookCallback;
        _keyboardCallback = KeyboardHookCallback;
        _dispatcher = new Control();
        _ = _dispatcher.Handle;
    }

    internal bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value)
            {
                ResetKeyboardCapture();
                _aeroSnapCandidate = default;
                ResetCaptionClick();
            }
        }
    }

    internal void Start()
    {
        if (_mouseHook != 0 || _keyboardHook != 0)
        {
            return;
        }

        var module = NativeMethods.GetModuleHandleW(null);
        _mouseHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhMouseLl, _mouseCallback, module, 0);
        if (_mouseHook == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The mouse hook could not be installed.");
        }

        _keyboardHook = NativeMethods.SetWindowsHookExW(NativeMethods.WhKeyboardLl, _keyboardCallback, module, 0);
        if (_keyboardHook == 0)
        {
            var error = Marshal.GetLastWin32Error();
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
            throw new Win32Exception(error, "The keyboard hook could not be installed.");
        }

        for (var virtualKey = 1; virtualKey < 256; virtualKey++)
        {
            if ((NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0)
            {
                _physicalKeysDown.Add((uint)virtualKey);
            }
        }
    }

    public void Dispose()
    {
        ResetKeyboardCapture();

        if (_keyboardHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = 0;
        }

        if (_mouseHook != 0)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = 0;
        }

        _dispatcher.Dispose();
    }

    private nint MouseHookCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0 || !Enabled)
        {
            return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
        }

        try
        {
            var message = (uint)wParam;
            if (message is not (NativeMethods.WmLButtonDown or NativeMethods.WmLButtonUp))
            {
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            }

            var mouse = Marshal.PtrToStructure<NativeMethods.MouseLowLevelHookData>(lParam);
            var aeroSnapCandidate = _aeroSnapCandidate;
            if (message == NativeMethods.WmLButtonDown || message == NativeMethods.WmLButtonUp)
            {
                _aeroSnapCandidate = default;
            }

            if (message == NativeMethods.WmLButtonUp)
            {
                if (aeroSnapCandidate.Window != 0 &&
                    IsDragGesture(mouse.Position) &&
                    _windowController.TryCancelAeroSnapRelease(
                        aeroSnapCandidate,
                        mouse.Position,
                        out var workArea))
                {
                    ResetCaptionClick();
                    Dispatch(() => _windowController.CompleteInterceptedAeroSnap(aeroSnapCandidate, workArea));
                    return 1;
                }
            }

            if (!_windowController.TryGetMsrdcWindowAt(mouse.Position, out var window, out var hitTest))
            {
                ResetCaptionClick();
                return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
            }

            if (message == NativeMethods.WmLButtonDown && hitTest == NativeMethods.HtCaption)
            {
                _aeroSnapCandidate = _windowController.TryCaptureAeroSnapCandidate(window, out var candidate)
                    ? candidate
                    : default;
                _aeroSnapStartPosition = mouse.Position;
                DetectCaptionDoubleClick(window, mouse.Position, mouse.Time);
            }
            else if (message == NativeMethods.WmLButtonDown)
            {
                _aeroSnapCandidate = default;
                ResetCaptionClick();
            }
        }
        catch
        {
            _aeroSnapCandidate = default;
            ResetCaptionClick();
        }

        return NativeMethods.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private bool IsDragGesture(NativeMethods.Point position)
    {
        return Math.Abs(position.X - _aeroSnapStartPosition.X) >= NativeMethods.GetSystemMetrics(NativeMethods.SmCxDrag) ||
            Math.Abs(position.Y - _aeroSnapStartPosition.Y) >= NativeMethods.GetSystemMetrics(NativeMethods.SmCyDrag);
    }

    private nint KeyboardHookCallback(int code, nuint wParam, nint lParam)
    {
        if (code < 0)
        {
            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        }

        uint currentVirtualKey = 0;
        try
        {
            var message = (uint)wParam;
            if (message is not (NativeMethods.WmKeyDown or NativeMethods.WmKeyUp or NativeMethods.WmSysKeyDown or NativeMethods.WmSysKeyUp))
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            var key = Marshal.PtrToStructure<NativeMethods.KeyboardLowLevelHookData>(lParam);
            currentVirtualKey = key.VirtualKey;
            if ((key.Flags & NativeMethods.LlkhfInjected) != 0 && key.ExtraInfo == ReplayInputMarker)
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            var keyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            var previouslyPhysicallyDown = _physicalKeysDown.Contains(key.VirtualKey);
            if (keyDown)
            {
                _physicalKeysDown.Add(key.VirtualKey);
            }
            else
            {
                _physicalKeysDown.Remove(key.VirtualKey);
            }

            if (_suppressedPhysicalKeys.Contains(key.VirtualKey))
            {
                if (!keyDown)
                {
                    _suppressedPhysicalKeys.Remove(key.VirtualKey);
                }

                return 1;
            }

            if (!Enabled)
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            var isWindowsKey = key.VirtualKey is NativeMethods.VkLWin or NativeMethods.VkRWin;
            if (_pendingNormalWindowsKey != 0)
            {
                if (isWindowsKey && key.VirtualKey == _pendingNormalWindowsKey)
                {
                    if (!keyDown)
                    {
                        var pairInserted = ReplayPendingWindowsKey(includeKeyUp: true);
                        if (pairInserted < 2)
                        {
                            return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                        }
                    }

                    return 1;
                }

                if (keyDown &&
                    !previouslyPhysicallyDown &&
                    key.VirtualKey == NativeMethods.VkUp &&
                    _physicalKeysDown.All(
                        virtualKey => virtualKey == NativeMethods.VkUp ||
                            virtualKey == _pendingNormalWindowsKey) &&
                    _windowController.TryGetForegroundMsrdcWindow(out var normalWindow) &&
                    normalWindow == _pendingNormalWindowsWindow &&
                    !_windowController.IsPseudoMaximized(normalWindow) &&
                    !_windowController.IsNativeMaximized(normalWindow))
                {
                    _suppressedPhysicalKeys.Add(_pendingNormalWindowsKey);
                    _suppressedPhysicalKeys.Add(NativeMethods.VkUp);
                    ClearPendingWindowsKey();
                    Dispatch(() => _windowController.PseudoMaximize(normalWindow));
                    return 1;
                }

                var combinedInserted = ReplayPendingWindowsKeyWithCurrent(key, keyDown);
                if (combinedInserted == 2)
                {
                    return 1;
                }
            }

            if (isWindowsKey &&
                keyDown &&
                _windowController.TryGetForegroundMsrdcWindow(out var pendingWindow) &&
                !_windowController.IsPseudoMaximized(pendingWindow) &&
                !_windowController.IsNativeMaximized(pendingWindow))
            {
                _pendingNormalWindowsKey = key.VirtualKey;
                _pendingNormalWindowsScanCode = key.ScanCode;
                _pendingNormalWindowsFlags = key.Flags;
                _pendingNormalWindowsWindow = pendingWindow;
                return 1;
            }

        }
        catch
        {
            var captured = _pendingNormalWindowsKey != 0 ||
                _suppressedPhysicalKeys.Contains(currentVirtualKey);
            ResetKeyboardCapture();
            if (captured)
            {
                return 1;
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void DetectCaptionDoubleClick(nint window, NativeMethods.Point position, uint time)
    {
        var isDoubleClick = window == _lastCaptionWindow &&
            unchecked(time - _lastCaptionClickTime) <= NativeMethods.GetDoubleClickTime() &&
            Math.Abs(position.X - _lastCaptionPosition.X) <= NativeMethods.GetSystemMetrics(NativeMethods.SmCxDoubleClk) / 2 &&
            Math.Abs(position.Y - _lastCaptionPosition.Y) <= NativeMethods.GetSystemMetrics(NativeMethods.SmCyDoubleClk) / 2;

        if (isDoubleClick)
        {
            _windowController.AllowNativeFullscreen(window);
            ResetCaptionClick();
            return;
        }

        _lastCaptionWindow = window;
        _lastCaptionPosition = position;
        _lastCaptionClickTime = time;
    }

    private void ResetCaptionClick()
    {
        _lastCaptionWindow = 0;
        _lastCaptionClickTime = 0;
    }

    private void ResetKeyboardCapture()
    {
        if (_pendingNormalWindowsKey != 0)
        {
            ReplayPendingWindowsKey(includeKeyUp: false);
        }
    }

    private uint ReplayPendingWindowsKey(bool includeKeyUp)
    {
        if (_pendingNormalWindowsKey == 0)
        {
            return 0;
        }

        var keyDown = CreateReplayInput(_pendingNormalWindowsKey, _pendingNormalWindowsScanCode, _pendingNormalWindowsFlags, keyUp: false);
        NativeMethods.Input[] inputs;
        if (includeKeyUp)
        {
            var keyUp = CreateReplayInput(_pendingNormalWindowsKey, _pendingNormalWindowsScanCode, _pendingNormalWindowsFlags, keyUp: true);
            inputs = [keyDown, keyUp];
        }
        else
        {
            inputs = [keyDown];
        }

        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        ClearPendingWindowsKey();
        return sent;
    }

    private uint ReplayPendingWindowsKeyWithCurrent(
        in NativeMethods.KeyboardLowLevelHookData currentKey,
        bool currentKeyDown)
    {
        var windowsKeyDown = CreateReplayInput(
            _pendingNormalWindowsKey,
            _pendingNormalWindowsScanCode,
            _pendingNormalWindowsFlags,
            keyUp: false);
        var currentInput = CreateReplayInput(
            currentKey.VirtualKey,
            currentKey.ScanCode,
            currentKey.Flags,
            keyUp: !currentKeyDown);
        var inputs = new[] { windowsKeyDown, currentInput };
        var sent = NativeMethods.SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<NativeMethods.Input>());
        ClearPendingWindowsKey();
        return sent;
    }

    private static NativeMethods.Input CreateReplayInput(uint virtualKey, uint scanCode, uint hookFlags, bool keyUp)
    {
        var flags = (hookFlags & NativeMethods.LlkhfExtended) != 0
            ? NativeMethods.KeyEventExtendedKey
            : 0;
        if (keyUp)
        {
            flags |= NativeMethods.KeyEventKeyUp;
        }

        return new NativeMethods.Input
        {
            Type = NativeMethods.InputKeyboard,
            Data = new NativeMethods.InputUnion
            {
                Keyboard = new NativeMethods.KeyboardInput
                {
                    VirtualKey = (ushort)virtualKey,
                    ScanCode = (ushort)scanCode,
                    Flags = flags,
                    ExtraInfo = ReplayInputMarker,
                },
            },
        };
    }

    private void ClearPendingWindowsKey()
    {
        _pendingNormalWindowsKey = 0;
        _pendingNormalWindowsScanCode = 0;
        _pendingNormalWindowsFlags = 0;
        _pendingNormalWindowsWindow = 0;
    }

    private void Dispatch(Action action)
    {
        if (!_dispatcher.IsDisposed)
        {
            _dispatcher.BeginInvoke(action);
        }
    }

}
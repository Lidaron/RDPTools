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
    private readonly Dictionary<uint, MsrdcWindowController.KeyboardTarget> _capturedSystemKeys = [];

    private nint _mouseHook;
    private nint _keyboardHook;
    private nint _lastCaptionWindow;
    private MsrdcWindowController.KeyboardTarget _windowsChordTarget;
    private MsrdcWindowController.AeroSnapCandidate _aeroSnapCandidate;
    private NativeMethods.Point _lastCaptionPosition;
    private NativeMethods.Point _aeroSnapStartPosition;
    private uint _lastCaptionClickTime;
    private bool _leftWindowsDown;
    private bool _rightWindowsDown;
    private bool _leftAltDown;
    private bool _rightAltDown;
    private bool _leftControlDown;
    private bool _rightControlDown;
    private bool _consumeNormalWindowWinUp;
    private bool _consumeNormalWindowWindowsRelease;
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

    }

    public void Dispose()
    {
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

        try
        {
            var message = (uint)wParam;
            if (message is not (NativeMethods.WmKeyDown or NativeMethods.WmKeyUp or NativeMethods.WmSysKeyDown or NativeMethods.WmSysKeyUp))
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            var key = Marshal.PtrToStructure<NativeMethods.KeyboardLowLevelHookData>(lParam);
            if ((key.Flags & NativeMethods.LlkhfInjected) != 0 && key.ExtraInfo == ReplayInputMarker)
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            var keyDown = message is NativeMethods.WmKeyDown or NativeMethods.WmSysKeyDown;
            UpdateModifierState(key.VirtualKey, keyDown);

            if (!Enabled)
            {
                return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }

            var isWindowsKey = key.VirtualKey is NativeMethods.VkLWin or NativeMethods.VkRWin;
            nint pseudoWindow = 0;
            if (_windowsChordTarget.RootWindow != 0 ||
                (isWindowsKey && keyDown &&
                  _windowController.TryGetForegroundMsrdcWindow(out pseudoWindow) &&
                 _windowController.IsPseudoMaximized(pseudoWindow)))
            {
                if (_windowsChordTarget.RootWindow == 0 &&
                    !_windowController.TryGetKeyboardTarget(pseudoWindow, out _windowsChordTarget))
                {
                    return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                if (!_windowController.TryPostKey(_windowsChordTarget, message, key))
                {
                    ResetKeyboardCapture();
                    return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
                }

                if (!keyDown && isWindowsKey && !_leftWindowsDown && !_rightWindowsDown)
                {
                    _windowsChordTarget = default;
                }

                return 1;
            }

            if (_pendingNormalWindowsKey != 0)
            {
                if (isWindowsKey && key.VirtualKey == _pendingNormalWindowsKey)
                {
                    if (!keyDown)
                    {
                        ReplayPendingWindowsKey(includeKeyUp: true);
                    }

                    return 1;
                }

                if (keyDown &&
                    key.VirtualKey == NativeMethods.VkUp &&
                    _windowController.TryGetForegroundMsrdcWindow(out var normalWindow) &&
                    normalWindow == _pendingNormalWindowsWindow &&
                    !_windowController.IsPseudoMaximized(normalWindow) &&
                    !_windowController.IsNativeMaximized(normalWindow))
                {
                    ClearPendingWindowsKey();
                    _consumeNormalWindowWinUp = true;
                    _consumeNormalWindowWindowsRelease = true;
                    Dispatch(() => _windowController.PseudoMaximize(normalWindow));
                    return 1;
                }

                var replayed = ReplayPendingWindowsKeyWithCurrent(key, keyDown);
                if (replayed)
                {
                    return 1;
                }

                _consumeNormalWindowWindowsRelease = true;
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

            if (key.VirtualKey == NativeMethods.VkUp && _consumeNormalWindowWinUp)
            {
                if (!keyDown)
                {
                    _consumeNormalWindowWinUp = false;
                }

                return 1;
            }

            if (isWindowsKey && _consumeNormalWindowWindowsRelease && !keyDown)
            {
                if (!_leftWindowsDown && !_rightWindowsDown)
                {
                    _consumeNormalWindowWindowsRelease = false;
                }

                return 1;
            }

            if (TryGetCapturedSystemChord(key.VirtualKey, keyDown, out var systemChordTarget))
            {
                return _windowController.TryPostKey(systemChordTarget, message, key)
                    ? 1
                    : NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
            }
        }
        catch
        {
            ResetKeyboardCapture();
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private bool TryGetCapturedSystemChord(
        uint virtualKey,
        bool keyDown,
        out MsrdcWindowController.KeyboardTarget keyboardTarget)
    {
        if (_capturedSystemKeys.TryGetValue(virtualKey, out keyboardTarget))
        {
            if (!keyDown)
            {
                _capturedSystemKeys.Remove(virtualKey);
            }

            return true;
        }

        var isAltChord = (_leftAltDown || _rightAltDown) &&
            virtualKey is NativeMethods.VkTab or NativeMethods.VkEscape or NativeMethods.VkSpace or NativeMethods.VkF4;
        var isControlChord = (_leftControlDown || _rightControlDown) && virtualKey == NativeMethods.VkEscape;
        if (!keyDown || (!isAltChord && !isControlChord) ||
            !_windowController.TryGetForegroundMsrdcWindow(out var window) ||
            !_windowController.IsPseudoMaximized(window) ||
            !_windowController.TryGetKeyboardTarget(window, out keyboardTarget))
        {
            keyboardTarget = default;
            return false;
        }

        _capturedSystemKeys[virtualKey] = keyboardTarget;
        return true;
    }

    private void UpdateModifierState(uint virtualKey, bool keyDown)
    {
        switch (virtualKey)
        {
            case NativeMethods.VkLWin:
                _leftWindowsDown = keyDown;
                break;
            case NativeMethods.VkRWin:
                _rightWindowsDown = keyDown;
                break;
            case NativeMethods.VkLMenu:
                _leftAltDown = keyDown;
                break;
            case NativeMethods.VkRMenu:
                _rightAltDown = keyDown;
                break;
            case NativeMethods.VkLControl:
                _leftControlDown = keyDown;
                break;
            case NativeMethods.VkRControl:
                _rightControlDown = keyDown;
                break;
        }
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
        _windowsChordTarget = default;
        _capturedSystemKeys.Clear();
        _consumeNormalWindowWinUp = false;
        _consumeNormalWindowWindowsRelease = false;
        ClearPendingWindowsKey();
    }

    private bool ReplayPendingWindowsKey(bool includeKeyUp)
    {
        if (_pendingNormalWindowsKey == 0)
        {
            return true;
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
        return sent == inputs.Length;
    }

    private bool ReplayPendingWindowsKeyWithCurrent(
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
        return sent == inputs.Length;
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
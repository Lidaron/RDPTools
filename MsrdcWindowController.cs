using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace RDPTools;

internal sealed class MsrdcWindowController : IDisposable
{
    private readonly Dictionary<nint, ManagedWindow> _managedWindows = [];
    private readonly System.Windows.Forms.Timer _windowMonitor;

    internal MsrdcWindowController()
    {
        _windowMonitor = new System.Windows.Forms.Timer { Interval = 750 };
        _windowMonitor.Tick += (_, _) => ReconcileManagedWindows();
        _windowMonitor.Start();
    }

    internal bool IsPseudoMaximized(nint window)
    {
        return _managedWindows.TryGetValue(GetRootWindow(window), out var managedWindow) &&
            managedWindow.State == ManagedWindowState.PseudoMaximized;
    }

    internal bool IsNativeMaximized(nint window) => NativeMethods.IsZoomed(GetRootWindow(window));

    internal bool TryGetForegroundMsrdcWindow(out nint window)
    {
        window = GetRootWindow(NativeMethods.GetForegroundWindow());
        return IsSupportedRdpWindow(window);
    }

    internal bool TryGetMsrdcWindowAt(NativeMethods.Point position, out nint window, out int hitTest)
    {
        window = GetRootWindow(NativeMethods.WindowFromPoint(position));
        hitTest = 0;

        if (!IsSupportedRdpWindow(window))
        {
            return false;
        }

        var packedPosition = unchecked((int)((ushort)position.X | ((uint)(ushort)position.Y << 16)));
        if (NativeMethods.SendMessageTimeoutW(
                window,
                NativeMethods.WmNcHitTest,
                0,
                packedPosition,
                NativeMethods.SmtoAbortIfHung,
                100,
                out var result) == 0)
        {
            return false;
        }

        hitTest = unchecked((int)result);
        return true;
    }

    internal void PseudoMaximize(nint window)
    {
        window = GetRootWindow(window);
        if (!IsSupportedRdpWindow(window) || _managedWindows.ContainsKey(window) || NativeMethods.IsZoomed(window))
        {
            return;
        }

        var placement = new NativeMethods.WindowPlacement
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.WindowPlacement>(),
        };

        if (!NativeMethods.GetWindowPlacement(window, ref placement) ||
            !NativeMethods.GetWindowRect(window, out _))
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        var managedWindow = new ManagedWindow(processId, placement);
        _managedWindows.Add(window, managedWindow);
        if (!ApplyWorkArea(window, managedWindow))
        {
            _managedWindows.Remove(window);
        }
    }

    internal bool TryCaptureAeroSnapCandidate(nint window, out AeroSnapCandidate candidate)
    {
        window = GetRootWindow(window);
        candidate = default;
        if (!IsSupportedRdpWindow(window) ||
            _managedWindows.ContainsKey(window) ||
            NativeMethods.IsZoomed(window))
        {
            return false;
        }

        var placement = new NativeMethods.WindowPlacement
        {
            Length = (uint)Marshal.SizeOf<NativeMethods.WindowPlacement>(),
        };
        if (!NativeMethods.GetWindowPlacement(window, ref placement))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        candidate = new AeroSnapCandidate(window, processId, placement);
        return true;
    }

    internal bool TryCancelAeroSnapRelease(
        AeroSnapCandidate candidate,
        NativeMethods.Point position,
        out NativeMethods.Rect workArea)
    {
        workArea = default;
        var window = GetRootWindow(candidate.Window);
        if (window != candidate.Window ||
            !IsSupportedRdpWindow(window) ||
            _managedWindows.ContainsKey(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId != candidate.ProcessId)
        {
            return false;
        }

        var threadId = NativeMethods.GetWindowThreadProcessId(window, out _);
        var threadInfo = new NativeMethods.GuiThreadInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>(),
        };
        if (threadId == 0 ||
            !NativeMethods.GetGUIThreadInfo(threadId, ref threadInfo) ||
            (threadInfo.Flags & NativeMethods.GuiInMoveSize) == 0 ||
            GetRootWindow(threadInfo.MoveSizeWindow) != window)
        {
            return false;
        }

        var monitor = NativeMethods.MonitorFromPoint(position, NativeMethods.MonitorDefaultToNearest);
        var monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };
        if (monitor == 0 ||
            !NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo) ||
            position.X < monitorInfo.Monitor.Left ||
            position.X >= monitorInfo.Monitor.Right ||
            position.Y < monitorInfo.Monitor.Top ||
            position.Y > monitorInfo.Monitor.Top + 2)
        {
            return false;
        }

        workArea = monitorInfo.WorkArea;

        if (NativeMethods.SendMessageTimeoutW(
            threadInfo.MoveSizeWindow,
            NativeMethods.WmCancelMode,
            0,
            0,
            NativeMethods.SmtoAbortIfHung,
            100,
            out _) == 0)
        {
            return false;
        }

        threadInfo.Size = (uint)Marshal.SizeOf<NativeMethods.GuiThreadInfo>();
        return NativeMethods.GetGUIThreadInfo(threadId, ref threadInfo) &&
            ((threadInfo.Flags & NativeMethods.GuiInMoveSize) == 0 ||
             GetRootWindow(threadInfo.MoveSizeWindow) != window);
    }

    internal void CompleteInterceptedAeroSnap(
        AeroSnapCandidate candidate,
        NativeMethods.Rect workArea)
    {
        var window = GetRootWindow(candidate.Window);
        if (window != candidate.Window ||
            !IsSupportedRdpWindow(window) ||
            _managedWindows.ContainsKey(window))
        {
            return;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId != candidate.ProcessId)
        {
            return;
        }

        var managedWindow = new ManagedWindow(processId, candidate.OriginalPlacement);
        _managedWindows.Add(window, managedWindow);
        if (!ApplyWorkArea(window, managedWindow, workArea))
        {
            _managedWindows.Remove(window);
        }
    }

    internal void AllowNativeFullscreen(nint window)
    {
        window = GetRootWindow(window);
        if (_managedWindows.TryGetValue(window, out var managedWindow) &&
            SetPseudoMaximizedStyle(window, false))
        {
            managedWindow.State = ManagedWindowState.AwaitingNativeFullscreen;
            managedWindow.TransitionDeadline = Environment.TickCount64 + 2000;
        }
    }

    internal void RestoreAll()
    {
        foreach (var window in _managedWindows.Keys.ToArray())
        {
            Restore(window);
        }
    }

    internal readonly record struct AeroSnapCandidate(
        nint Window,
        uint ProcessId,
        NativeMethods.WindowPlacement OriginalPlacement);

    public void Dispose()
    {
        _windowMonitor.Stop();
        _windowMonitor.Dispose();
        RestoreAll();
    }

    private static nint GetRootWindow(nint window)
    {
        return window == 0 ? 0 : NativeMethods.GetAncestor(window, NativeMethods.GaRoot);
    }

    private static bool IsSupportedRdpWindow(nint window)
    {
        if (window == 0 || !NativeMethods.IsWindow(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (string.Equals(process.ProcessName, "msrdc", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return string.Equals(process.ProcessName, "Windows365", StringComparison.OrdinalIgnoreCase) &&
                HasWindowClass(window, "TscShellContainerClass");
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static bool HasWindowClass(nint window, string expectedClassName)
    {
        var className = new StringBuilder(256);
        return NativeMethods.GetClassNameW(window, className, className.Capacity) > 0 &&
            string.Equals(className.ToString(), expectedClassName, StringComparison.Ordinal);
    }

    private void Restore(nint window)
    {
        if (!_managedWindows.TryGetValue(window, out var managedWindow))
        {
            return;
        }

        if (!IsSameWindow(window, managedWindow))
        {
            _managedWindows.Remove(window);
            return;
        }

        var hadPseudoMaximizedStyle = managedWindow.State == ManagedWindowState.PseudoMaximized;
        if (hadPseudoMaximizedStyle && !SetPseudoMaximizedStyle(window, false))
        {
            return;
        }

        if (NativeMethods.SetWindowPlacement(window, managedWindow.OriginalPlacement))
        {
            _managedWindows.Remove(window);
        }
        else if (hadPseudoMaximizedStyle)
        {
            SetPseudoMaximizedStyle(window, true);
        }
    }

    private static bool IsSameWindow(nint window, ManagedWindow managedWindow)
    {
        if (!NativeMethods.IsWindow(window))
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(window, out var processId);
        return processId == managedWindow.ProcessId;
    }

    private static bool TryGetMonitorInfo(nint window, out NativeMethods.MonitorInfo monitorInfo)
    {
        var monitor = NativeMethods.MonitorFromWindow(window, NativeMethods.MonitorDefaultToNearest);
        monitorInfo = new NativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>(),
        };

        return monitor != 0 && NativeMethods.GetMonitorInfoW(monitor, ref monitorInfo);
    }

    private static NativeMethods.Rect GetWorkArea(nint window)
    {
        return TryGetMonitorInfo(window, out var monitorInfo) ? monitorInfo.WorkArea : default;
    }

    private static bool ApplyWorkArea(nint window, ManagedWindow managedWindow)
    {
        return ApplyWorkArea(window, managedWindow, GetWorkArea(window));
    }

    private static bool ApplyWorkArea(
        nint window,
        ManagedWindow managedWindow,
        NativeMethods.Rect workArea)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            return false;
        }

        NativeMethods.ShowWindow(window, NativeMethods.SwRestore);
        if (!SetPseudoMaximizedStyle(window, false))
        {
            return false;
        }

        if (NativeMethods.SetWindowPos(
                window,
                0,
                workArea.Left,
                workArea.Top,
                workArea.Width,
                workArea.Height,
                NativeMethods.SwpNoZOrder | NativeMethods.SwpNoActivate | NativeMethods.SwpFrameChanged) &&
            NativeMethods.GetWindowRect(window, out var appliedRectangle))
        {
            managedWindow.WorkArea = workArea;
            managedWindow.AppliedRectangle = appliedRectangle;
            if (SetPseudoMaximizedStyle(window, true))
            {
                return true;
            }
        }

        NativeMethods.SetWindowPlacement(window, managedWindow.OriginalPlacement);
        return false;
    }

    private void ReconcileManagedWindows()
    {
        foreach (var pair in _managedWindows.ToArray())
        {
            var window = pair.Key;
            var managedWindow = pair.Value;

            if (!IsSameWindow(window, managedWindow))
            {
                _managedWindows.Remove(window);
                continue;
            }

            if (managedWindow.State == ManagedWindowState.AwaitingNativeFullscreen)
            {
                if (NativeMethods.IsZoomed(window) ||
                    NativeMethods.GetWindowRect(window, out var transitioningRectangle) &&
                    transitioningRectangle != managedWindow.AppliedRectangle)
                {
                    managedWindow.State = ManagedWindowState.NativeFullscreen;
                }
                else if (Environment.TickCount64 >= managedWindow.TransitionDeadline)
                {
                    if (SetPseudoMaximizedStyle(window, true))
                    {
                        managedWindow.State = ManagedWindowState.PseudoMaximized;
                    }
                }

                continue;
            }

            if (managedWindow.State == ManagedWindowState.NativeFullscreen)
            {
                if (NativeMethods.IsZoomed(window) ||
                    !NativeMethods.GetWindowRect(window, out var restoredRectangle))
                {
                    continue;
                }

                if (restoredRectangle == managedWindow.AppliedRectangle)
                {
                    Restore(window);
                }
                else if (!IsFullscreenRectangle(window, restoredRectangle))
                {
                    _managedWindows.Remove(window);
                }

                continue;
            }

            if (NativeMethods.IsIconic(window))
            {
                continue;
            }

            var currentWorkArea = GetWorkArea(window);
            if (currentWorkArea != managedWindow.WorkArea)
            {
                if (!ApplyWorkArea(window, managedWindow))
                {
                    _managedWindows.Remove(window);
                }

                continue;
            }

            if (!NativeMethods.GetWindowRect(window, out var currentRectangle) ||
                currentRectangle != managedWindow.AppliedRectangle)
            {
                _managedWindows.Remove(window);
            }
        }
    }

    private static bool IsFullscreenRectangle(nint window, NativeMethods.Rect rectangle)
    {
        if (!TryGetMonitorInfo(window, out var monitorInfo))
        {
            return true;
        }

        return rectangle.Left <= monitorInfo.Monitor.Left &&
            rectangle.Top <= monitorInfo.Monitor.Top &&
            rectangle.Right >= monitorInfo.Monitor.Right &&
            rectangle.Bottom >= monitorInfo.Monitor.Bottom;
    }

    private static bool SetPseudoMaximizedStyle(nint window, bool maximized)
    {
        var style = NativeMethods.GetWindowLongPtrW(window, NativeMethods.GwlStyle).ToInt64();
        var updatedStyle = maximized
            ? style | NativeMethods.WsMaximize
            : style & ~NativeMethods.WsMaximize;
        if (updatedStyle == style)
        {
            return true;
        }

        NativeMethods.SetWindowLongPtrW(window, NativeMethods.GwlStyle, new nint(updatedStyle));
        var appliedStyle = NativeMethods.GetWindowLongPtrW(window, NativeMethods.GwlStyle).ToInt64();
        if (((appliedStyle & NativeMethods.WsMaximize) != 0) != maximized)
        {
            return false;
        }

        return NativeMethods.SetWindowPos(
            window,
            0,
            0,
            0,
            0,
            0,
            NativeMethods.SwpNoMove |
            NativeMethods.SwpNoSize |
            NativeMethods.SwpNoZOrder |
            NativeMethods.SwpNoActivate |
            NativeMethods.SwpFrameChanged);
    }

    private enum ManagedWindowState
    {
        PseudoMaximized,
        AwaitingNativeFullscreen,
        NativeFullscreen,
    }

    private sealed class ManagedWindow(uint processId, NativeMethods.WindowPlacement originalPlacement)
    {
        internal uint ProcessId { get; } = processId;
        internal NativeMethods.WindowPlacement OriginalPlacement { get; } = originalPlacement;
        internal NativeMethods.Rect WorkArea { get; set; }
        internal NativeMethods.Rect AppliedRectangle { get; set; }
        internal ManagedWindowState State { get; set; }
        internal long TransitionDeadline { get; set; }
    }
}
using System.Runtime.Versioning;
using Microsoft.Win32;
using Worklapse.Core.Platform;

namespace Worklapse.Capture.Windows;

/// <summary>Tracks whether the session is locked, from session-switch notifications.</summary>
[SupportedOSPlatform("windows")]
public sealed class Win32SessionStateMonitor : ISessionStateMonitor
{
    private volatile bool _locked;

    public Win32SessionStateMonitor()
    {
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    public bool IsLocked => _locked;

    public void Dispose() => SystemEvents.SessionSwitch -= OnSessionSwitch;

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        switch (e.Reason)
        {
            case SessionSwitchReason.SessionLock:
            case SessionSwitchReason.ConsoleDisconnect:
            case SessionSwitchReason.RemoteDisconnect:
                _locked = true;
                break;
            case SessionSwitchReason.SessionUnlock:
            case SessionSwitchReason.ConsoleConnect:
            case SessionSwitchReason.RemoteConnect:
                _locked = false;
                break;
        }
    }
}

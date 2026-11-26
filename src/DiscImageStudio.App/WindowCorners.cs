using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DiscImageStudio;

/// <summary>Keep Windows 10 square window geometry on newer Windows as well. DWM still
/// owns the native border, shadow and window transitions. Call once the handle exists.</summary>
internal static class WindowCorners
{
    private const int DwmWindowAttributeCornerPreference = 33;
    private const int DwmWindowCornerPreferenceDoNotRound = 1;
    private const int DwmWindowAttributeTransitionsForceDisabled = 3;

    internal static void Apply(Window window)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        int preference = DwmWindowCornerPreferenceDoNotRound;
        _ = DwmSetWindowAttribute(handle, DwmWindowAttributeCornerPreference, ref preference, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal static void EnableTransitions(IntPtr handle)
    {
        int forceDisabled = 0;
        _ = DwmSetWindowAttribute(handle, DwmWindowAttributeTransitionsForceDisabled, ref forceDisabled, sizeof(int));
    }
}

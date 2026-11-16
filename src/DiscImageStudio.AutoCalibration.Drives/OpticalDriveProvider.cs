namespace DiscImageStudio.AutoCalibration.Drives;

public static class OpticalDriveProvider
{
    public static IOpticalDriveProvider CreateForCurrentPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return new WindowsSptiOpticalDriveProvider();
        }

        if (OperatingSystem.IsLinux())
        {
            return new LinuxSgIoOpticalDriveProvider();
        }

        if (OperatingSystem.IsMacOS())
        {
            return new MacOsOpticalDriveProvider();
        }

        throw new PlatformNotSupportedException(
            "Optical-drive access is implemented only for Windows, Linux, and macOS.");
    }
}

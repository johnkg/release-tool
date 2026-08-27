using System.Runtime.Versioning;
using Microsoft.Win32;

namespace ReleaseTool.Api.Configuration;

/// <summary>
/// The operating system's own light/dark setting.
///
/// Why this exists: browsers do not always pass the OS setting through to
/// <c>prefers-color-scheme</c>. Chrome and Edge have an appearance setting of
/// their own, and when it is set to Light the page is told "light" however
/// Windows is configured - so "follow system" in the app followed the browser,
/// not the system.
///
/// Only meaningful when the browser and the server are the same machine, which
/// is why the config endpoint reports it to loopback callers only.
/// </summary>
public static class OsTheme
{
    /// <summary>"light", "dark", or null when the setting cannot be read.</summary>
    public static string? Read() => OperatingSystem.IsWindows() ? ReadWindows() : null;

    [SupportedOSPlatform("windows")]
    private static string? ReadWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");

            // AppsUseLightTheme is the one that governs application content.
            // SystemUsesLightTheme covers the taskbar and Start menu instead.
            return key?.GetValue("AppsUseLightTheme") is int light ? (light == 0 ? "dark" : "light") : null;
        }
        catch (Exception)
        {
            // A locked-down profile or a missing key is not worth failing over;
            // the browser's own media query still answers.
            return null;
        }
    }
}

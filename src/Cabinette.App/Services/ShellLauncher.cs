using System.Diagnostics;
using Cabinette.Windows;

namespace Cabinette.App.Services;

/// <summary>Opens folders in Explorer through <see cref="IShellOperations"/>, with a plain Explorer fallback.</summary>
public static class ShellLauncher
{
    public static void OpenFolder(IShellOperations shell, string folder)
    {
        try
        {
            shell.OpenWithDefaultApp(folder);
            return;
        }
        catch (Exception ex) when (ex is NotImplementedException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            AppLog.Info($"Shell open failed for '{folder}', falling back to explorer.exe ({ex.GetType().Name}).");
        }

        try
        {
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"Opening '{folder}' in Explorer failed.", ex);
        }
    }
}

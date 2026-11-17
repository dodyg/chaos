using Chaos.Application.Abstractions;

namespace Chaos.Desktop.Platform;

/// <summary>Opens the Google sign-in page in the person's default web browser. Only https addresses are ever passed in.</summary>
public sealed class ShellBrowser : ISystemBrowser
{
    public bool Open(Uri url)
    {
        if (url.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }
}

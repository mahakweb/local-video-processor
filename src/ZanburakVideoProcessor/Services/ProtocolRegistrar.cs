using Microsoft.Win32;
using System.IO;
using System.Text.RegularExpressions;

namespace ZanburakVideoProcessor.Services;

public static class ProtocolRegistrar
{
    public const string Protocol = "zanburak-video";

    public static void EnsureCurrentUser()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                return;
            }

            using var key = Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Protocol}");
            key?.SetValue(string.Empty, "URL:Zanburak Video Processor");
            key?.SetValue("URL Protocol", string.Empty);
            using var icon = key?.CreateSubKey("DefaultIcon");
            icon?.SetValue(string.Empty, $"\"{exe}\",0");
            using var command = key?.CreateSubKey(@"shell\open\command");
            command?.SetValue(string.Empty, $"\"{exe}\" \"%1\"");
        }
        catch
        {
            // Protocol registration is best-effort; installer script can retry.
        }
    }

    public static string? ParseTaskId(string[] args)
    {
        foreach (var arg in args)
        {
            var id = ParseTaskId(arg);
            if (!string.IsNullOrWhiteSpace(id))
            {
                return id;
            }
        }

        return null;
    }

    public static string? ParseApiBase(string[] args)
    {
        foreach (var arg in args)
        {
            var api = ParseApiBase(arg);
            if (!string.IsNullOrWhiteSpace(api))
            {
                return api;
            }
        }

        return null;
    }

    public static string? ParseApiBase(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"[?&]api=([^&\s]+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            return null;
        }

        var raw = Uri.UnescapeDataString(match.Groups[1].Value.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(raw) ? null : raw.TrimEnd('/');
    }

    public static string? ParseTaskId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"task=([0-9a-fA-F-]{36})");
        return match.Success ? match.Groups[1].Value : null;
    }
}

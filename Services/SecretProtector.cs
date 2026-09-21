using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IptvPlayer.Services;


public static class SecretProtector
{
    public const string Prefix = "dpapi:";

    public static string? Protect(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        var encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(value), null,
            DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(encrypted);
    }

    public static string? Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        try
        {
            var decrypted = ProtectedData.Unprotect(
                Convert.FromBase64String(value[Prefix.Length..]), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(decrypted);
        }
        catch (Exception ex)
        {
            // Null signals failure so callers keep the original protected value
            Serilog.Log.Warning(ex, "DPAPI: не удалось расшифровать значение.");
            return null;
        }
    }

    private static readonly Regex KeyFieldRegex = new(
        "\"key\"\\s*:\\s*\"[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*\"",
        RegexOptions.Compiled);

    private static readonly Regex CredentialParamRegex = new(
        "(username|password|token|key|pass)=([^&\\s\"']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MediaUrlRegex = new(
        "https?://[^\\s\"'\\\\]+\\.(?:m3u8|mp4|ts)[^\\s\"'\\\\]*",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);


    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var masked = KeyFieldRegex.Replace(value, "\"key\":\"***\"");
        masked = MediaUrlRegex.Replace(masked, "***");
        return CredentialParamRegex.Replace(masked, "$1=***");
    }
}

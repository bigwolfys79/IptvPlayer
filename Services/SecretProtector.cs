using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace IptvPlayer.Services;

/// <summary>
/// Шифрование чувствительных строк (ключ портала, URL плейлистов с
/// username/password) через DPAPI Windows, scope CurrentUser: расшифровать
/// может только тот же пользователь на том же ПК. Формат в файле настроек:
/// "dpapi:" + base64. Значения без префикса считаются legacy-plaintext и
/// пропускаются как есть — миграция старых settings.json происходит
/// автоматически при первом же сохранении.
/// </summary>
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
        catch (Exception)
        {
            // Другой пользователь/ПК или повреждённая запись — ключ
            // восстановить нельзя; возвращаем пустую строку вместо мусора.
            return string.Empty;
        }
    }

    private static readonly Regex KeyFieldRegex = new(
        "\"key\"\\s*:\\s*\"[^\"\\\\]*(?:\\\\.[^\"\\\\]*)*\"",
        RegexOptions.Compiled);

    private static readonly Regex CredentialParamRegex = new(
        "(username|password|token)=([^&\\s\"']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Маскирует секреты в строке для записи в логи/dump: значение поля
    /// "key" в JSON-теле запроса портала и параметры username/password/token
    /// в query-строке URL плейлиста или EPG-источника.
    /// </summary>
    public static string Mask(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value ?? string.Empty;
        }

        var masked = KeyFieldRegex.Replace(value, "\"key\":\"***\"");
        return CredentialParamRegex.Replace(masked, "$1=***");
    }
}

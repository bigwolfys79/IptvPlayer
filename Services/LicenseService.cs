using System;
using System.IO;
using System.Linq;
using System.Management;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using IptvPlayer.Models;
using Microsoft.Win32;
using Serilog;

namespace IptvPlayer.Services;


public static class LicenseService
{
    private const string RegPath = @"SOFTWARE\IptvPlayer";
    private const int TrialDays = 30;
    private const string TokenSeparator = "|";

    private const string LicenseKeyValueName = "LicenseKey";


    private const string LastSeenValueName = "LastSeenUtc";
    private const string UserRegPath = @"SOFTWARE\IptvPlayer";


    private static readonly TimeSpan ClockSkewTolerance = TimeSpan.FromHours(1);

    private static readonly byte[] HmacKey = Encoding.UTF8.GetBytes(
        "IptvPlayer-Lic-2024-Salt-k7Xm9pQ2wL");


    private const string PublicKeyXml =
        "<RSAKeyValue><Modulus>6Xu4JlI0aGBUZ07SIZ3Mon9wy9EvTV18GcL5f0OBQUWaVn5nZqG6/tk+Ms1HWdkxkRXMxiHWoouRplIIFnOJsASsyRr0RGH/R80nRQPbflzVV11N2D/tDp6wWuyiQ+gwzwOcamoE03Z2TI4r1JapiUpCz4qpH1JgTKoV1m5xOcrCMCTV+9SDb5rB52iRdZvhmBkxUPyiB6DB2LHrcOlvFg+12KY0SducDrUBJADA8t4qPBy3FbS5eeYjZ7Skwk1f46Rfure+soy0TrFBUtZCdznCpTfFkYZK6L9BsiA2wsphabq40xtfIYWxUvxPlEhIJIVz/bKbbWeH1Ng9ljyi8Q==</Modulus><Exponent>AQAB</Exponent></RSAKeyValue>";


    public static LicenseInfo CheckLicense()
    {
        try
        {

            var storedKey = ReadFirstAvailable(LicenseKeyValueName);
            if (!string.IsNullOrEmpty(storedKey))
            {
                var data = ValidateLicenseText(storedKey);
                if (data != null)
                {
                    var now = GetMonotonicNow();
                    var expired = data.ExpiryUtc.HasValue && now >= data.ExpiryUtc.Value;

                    Log.Information(
                        "Активированная лицензия: licensee={Licensee}, expiry={Expiry}, expired={Expired}",
                        data.Licensee, data.ExpiryUtc?.ToString("yyyy-MM-dd") ?? "бессрочно", expired);

                    return new LicenseInfo
                    {
                        UsageType = UsageType.Commercial,
                        IsActivated = true,
                        Licensee = data.Licensee,
                        ExpiryUtc = data.ExpiryUtc,
                        IsExpired = expired
                    };
                }

                Log.Warning("Сохранённый ключ лицензии не прошёл проверку — откат к trial-логике.");
            }

            var usageType = ReadRegString("UsageType");
            if (!string.Equals(usageType, "Commercial", StringComparison.OrdinalIgnoreCase))
            {
                return new LicenseInfo { UsageType = UsageType.Personal };
            }

            var encryptedToken = ReadRegString("LicenseToken");
            if (string.IsNullOrEmpty(encryptedToken))
            {
                return CreateFirstRunToken();
            }

            var token = DecryptToken(encryptedToken);
            if (token == null || !ValidateToken(token))
            {
                Log.Warning("Повреждён или подменён токен лицензии.");
                return new LicenseInfo
                {
                    UsageType = UsageType.Commercial,
                    IsExpired = true,
                    DaysRemaining = 0
                };
            }

            var elapsed = (GetMonotonicNow() - token.InstallDate).Days;
            var remaining = Math.Max(0, TrialDays - elapsed);

            return new LicenseInfo
            {
                UsageType = UsageType.Commercial,
                InstallDateUtc = token.InstallDate,
                DaysRemaining = remaining,
                IsExpired = remaining <= 0
            };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Ошибка проверки лицензии.");
            return new LicenseInfo
            {
                UsageType = UsageType.Commercial,
                IsExpired = true,
                DaysRemaining = 0
            };
        }
    }


    // HWID computed once — WMI query is expensive
    private static readonly Lazy<string> HwidCode = new(() => FormatHwidCode(ComputeHardwareHash()));
    private static readonly Lazy<string> HardwareId = new(() => Convert.ToBase64String(ComputeHardwareHash()));

    public static string GetHwidCode() => HwidCode.Value;


    public static ActivationResult Activate(string licenseText)
    {
        if (string.IsNullOrWhiteSpace(licenseText))
        {
            return ActivationResult.Fail(ActivationError.Empty);
        }

        var normalized = new string(
            licenseText.Where(c => !char.IsWhiteSpace(c)).ToArray());

        var data = ValidateLicenseText(normalized);
        if (data == null)
        {
            return ActivationResult.Fail(ActivationError.InvalidSignature);
        }


        if (!string.IsNullOrEmpty(data.Hwid))
        {
            var currentHwid = GetHwidCode();
            if (!string.Equals(data.Hwid, currentHwid, StringComparison.OrdinalIgnoreCase))
            {
                Log.Warning("Лицензия выпущена для другой машины: license={LicenseHwid}, this={CurrentHwid}",
                    data.Hwid, currentHwid);
                return ActivationResult.Fail(ActivationError.WrongMachine);
            }
        }

        if (data.ExpiryUtc.HasValue && DateTime.UtcNow >= data.ExpiryUtc.Value)
        {
            return ActivationResult.Fail(ActivationError.Expired);
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UserRegPath);
            key?.SetValue(LicenseKeyValueName, normalized, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "Не удалось сохранить лицензию в реестр.");
            return ActivationResult.Fail(ActivationError.StorageFailed);
        }

        Log.Information("Лицензия активирована: licensee={Licensee}, expiry={Expiry}",
            data.Licensee, data.ExpiryUtc?.ToString("yyyy-MM-dd") ?? "бессрочно");
        return ActivationResult.Ok(data);
    }


    public static LicenseData? ValidateLicenseText(string licenseText)
    {
        try
        {

            var parts = licenseText.Split('.', 3);
            if (parts.Length != 3 || parts[0] != "IPL1") return null;

            var payloadBytes = Base64UrlDecode(parts[1]);
            var signature = Base64UrlDecode(parts[2]);

            using var rsa = RSA.Create();
            rsa.FromXmlString(PublicKeyXml);
            if (!rsa.VerifyData(payloadBytes, signature, HashAlgorithmName.SHA256,
                    RSASignaturePadding.Pkcs1))
            {
                Log.Warning("Подпись лицензии не совпадает.");
                return null;
            }

            var json = Encoding.UTF8.GetString(payloadBytes);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var licensee = root.TryGetProperty("licensee", out var l) ? l.GetString() ?? "" : "";
            var hwid = root.TryGetProperty("hwid", out var h) ? h.GetString() ?? "" : "";
            var exp = root.TryGetProperty("exp", out var e) && e.TryGetInt64(out var expUnix)
                ? expUnix
                : 0;

            return new LicenseData
            {
                Licensee = licensee,
                Hwid = hwid,
                ExpiryUtc = exp > 0 ? DateTimeOffset.FromUnixTimeSeconds(exp).UtcDateTime : null
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Некорректный текст лицензии.");
            return null;
        }
    }


    private static DateTime GetMonotonicNow()
    {
        var now = DateTime.UtcNow;
        var lastSeen = ReadLastSeen();
        if (lastSeen.HasValue && lastSeen.Value - now > ClockSkewTolerance)
        {
            Log.Warning(
                "Часы откат назад: system={System:O}, lastSeen={LastSeen:O} — использую lastSeen.",
                now, lastSeen.Value);
            now = lastSeen.Value;
        }

        if (!lastSeen.HasValue || now > lastSeen.Value)
        {
            WriteLastSeen(now);
        }

        return now;
    }

    private static DateTime? ReadLastSeen()
    {
        try
        {
            var stored = Registry.GetValue($@"HKEY_CURRENT_USER\{UserRegPath}",
                LastSeenValueName, null) as string;
            if (string.IsNullOrEmpty(stored)) return null;

            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(stored),
                Encoding.UTF8.GetBytes(LastSeenValueName), DataProtectionScope.CurrentUser);
            return DateTime.FromBinary(BitConverter.ToInt64(bytes, 0));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "LastSeen не прочитан (первый запуск?).");
            return null;
        }
    }

    private static void WriteLastSeen(DateTime utc)
    {
        try
        {
            var bytes = BitConverter.GetBytes(utc.ToBinary());
            var encrypted = ProtectedData.Protect(bytes,
                Encoding.UTF8.GetBytes(LastSeenValueName), DataProtectionScope.CurrentUser);
            using var key = Registry.CurrentUser.CreateSubKey(UserRegPath);
            key?.SetValue(LastSeenValueName, Convert.ToBase64String(encrypted),
                RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось сохранить LastSeen.");
        }
    }

    private static byte[] ComputeHardwareHash()
    {
        var volumeSerial = GetVolumeSerial();
        var machineGuid = GetMachineGuid();
        using var sha = SHA256.Create();
        return sha.ComputeHash(Encoding.UTF8.GetBytes($"{volumeSerial}|{machineGuid}"));
    }

    private static string GenerateHardwareId()
        => HardwareId.Value;


    private static string FormatHwidCode(byte[] hash)
    {
        var hex = Convert.ToHexString(hash, 0, 8);
        return string.Join('-',
            hex.Substring(0, 4), hex.Substring(4, 4),
            hex.Substring(8, 4), hex.Substring(12, 4));
    }

    private static string GetVolumeSerial()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT VolumeSerialNumber FROM Win32_LogicalDisk WHERE DeviceID='C:'");
            foreach (var obj in searcher.Get())
            {
                return obj["VolumeSerialNumber"]?.ToString() ?? "unknown";
            }
        }
        catch { }
        return "unknown";
    }

    private static string GetMachineGuid()
    {
        try
        {
            return Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography",
                "MachineGuid", "")?.ToString() ?? "unknown";
        }
        catch { }
        return "unknown";
    }

    private static LicenseInfo CreateFirstRunToken()
    {
        var installDate = DateTime.UtcNow;
        var hwId = GenerateHardwareId();
        var timestamp = new DateTimeOffset(installDate).ToUnixTimeSeconds();
        var payload = $"{timestamp}{TokenSeparator}{hwId}";
        var hmac = ComputeHmac(payload);
        var tokenPlain = $"{payload}{TokenSeparator}{hmac}";
        var encrypted = EncryptToken(tokenPlain);

        WriteRegString("LicenseToken", encrypted);

        Log.Information("Создан токен лицензии: дата={Date}, HWID={HWID}",
            installDate, hwId);

        return new LicenseInfo
        {
            UsageType = UsageType.Commercial,
            InstallDateUtc = installDate,
            DaysRemaining = TrialDays,
            IsExpired = false
        };
    }

    private static LicenseToken? DecryptToken(string encryptedBase64)
    {
        try
        {
            var encrypted = Convert.FromBase64String(encryptedBase64);
            var plainBytes = ProtectedData.Unprotect(
                encrypted, HmacKey, DataProtectionScope.LocalMachine);
            var plain = Encoding.UTF8.GetString(plainBytes);

            var parts = plain.Split(new[] { TokenSeparator }, 3,
                StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 3) return null;

            if (!long.TryParse(parts[0], out var unixTimestamp)) return null;

            return new LicenseToken
            {
                InstallDate = DateTimeOffset.FromUnixTimeSeconds(unixTimestamp).UtcDateTime,
                HardwareId = parts[1],
                Hmac = parts[2]
            };
        }
        catch
        {
            return null;
        }
    }

    private static bool ValidateToken(LicenseToken token)
    {
        var payload = $"{new DateTimeOffset(token.InstallDate).ToUnixTimeSeconds()}{TokenSeparator}{token.HardwareId}";
        var expectedHmac = ComputeHmac(payload);

        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token.Hmac),
                Encoding.UTF8.GetBytes(expectedHmac)))
        {
            Log.Warning("HMAC токена лицензии не совпадает — подмена.");
            return false;
        }

        var currentHwId = GenerateHardwareId();
        if (!string.Equals(token.HardwareId, currentHwId, StringComparison.Ordinal))
        {
            Log.Warning("Hardware ID не совпадает: stored={Stored} current={Current}",
                token.HardwareId, currentHwId);
            return false;
        }

        return true;
    }

    private static string ComputeHmac(string data)
    {
        using var hmac = new HMACSHA256(HmacKey);
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToBase64String(hash);
    }

    private static string EncryptToken(string plainText)
    {
        var plainBytes = Encoding.UTF8.GetBytes(plainText);
        var encrypted = ProtectedData.Protect(
            plainBytes, HmacKey, DataProtectionScope.LocalMachine);
        return Convert.ToBase64String(encrypted);
    }

    // HKCU first (writable without admin), HKLM only as legacy fallback
    private static string? ReadRegString(string valueName)
    {
        try
        {
            var v = Registry.GetValue($@"HKEY_CURRENT_USER\{UserRegPath}", valueName, null)?.ToString();
            if (!string.IsNullOrEmpty(v)) return v;
        }
        catch { }

        try
        {
            return Registry.GetValue($@"HKEY_LOCAL_MACHINE\{RegPath}", valueName, null)?.ToString();
        }
        catch { return null; }
    }


    private static string? ReadFirstAvailable(string valueName)
    {
        return ReadRegString(valueName);
    }

    // Writes go to HKCU only — HKLM requires admin rights
    private static void WriteRegString(string valueName, string value)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(UserRegPath);
            key?.SetValue(valueName, value, RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Не удалось записать {ValueName} в реестр.", valueName);
        }
    }

    private static byte[] Base64UrlDecode(string s)
    {
        var base64 = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(base64.PadRight(
            base64.Length + (4 - base64.Length % 4) % 4, '='));
    }

    public sealed class LicenseData
    {
        public string Licensee { get; set; } = string.Empty;
        public string Hwid { get; set; } = string.Empty;
        public DateTime? ExpiryUtc { get; set; }
    }

    public enum ActivationError
    {
        None,
        Empty,
        InvalidSignature,
        WrongMachine,
        Expired,
        StorageFailed
    }

    public sealed class ActivationResult
    {
        public bool Success { get; init; }
        public ActivationError Error { get; init; }
        public LicenseData? License { get; init; }

        public static ActivationResult Ok(LicenseData license) =>
            new() { Success = true, License = license };
        public static ActivationResult Fail(ActivationError error) =>
            new() { Success = false, Error = error };
    }

    private sealed class LicenseToken
    {
        public DateTime InstallDate { get; set; }
        public string HardwareId { get; set; } = string.Empty;
        public string Hmac { get; set; } = string.Empty;
    }
}

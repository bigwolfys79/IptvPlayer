using System;

namespace IptvPlayer.Models;

public enum UsageType
{
    Personal,
    Commercial
}

public class LicenseInfo
{
    public UsageType UsageType { get; set; } = UsageType.Personal;
    public bool IsExpired { get; set; }
    public int DaysRemaining { get; set; }
    public DateTime? InstallDateUtc { get; set; }

    /// <summary>
    /// Купленная офлайн-лицензия активирована (подписанный ключ прошёл
    /// проверку). Срок тогда смотрят в ExpiryUtc, а не в DaysRemaining.
    /// </summary>
    public bool IsActivated { get; set; }

    /// <summary>Имя/организация покупателя из лицензии.</summary>
    public string Licensee { get; set; } = string.Empty;

    /// <summary>Срок действия лицензии; null = бессрочная.</summary>
    public DateTime? ExpiryUtc { get; set; }
}

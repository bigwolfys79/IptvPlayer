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


    public bool IsActivated { get; set; }


    public string Licensee { get; set; } = string.Empty;


    public DateTime? ExpiryUtc { get; set; }
}

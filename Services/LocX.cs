namespace IptvPlayer.Services;

// Static wrapper for x:Bind function syntax inside data templates
public static class LocX
{
    public static string T(string key) => L.T(key);
}

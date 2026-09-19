using Microsoft.Windows.ApplicationModel.Resources;

namespace IptvPlayer.Services;


public static class L
{
    private static ResourceManager? _manager;
    private static ResourceContext? _context;

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Cache = new();

    public static string Lang { get; private set; } = "ru";

    public static bool IsRussian => Lang != "en";


    public static void SetLanguage(string lang)
    {
        Lang = string.IsNullOrEmpty(lang) ? "ru" : lang;
        // Cached strings are language-bound — must not survive a switch
        Cache.Clear();
        try
        {
            _context = GetManager().CreateResourceContext();
            _context.QualifierValues["language"] = Lang == "en" ? "en-US" : "ru-RU";
        }
        catch (System.Exception ex)
        {


            Serilog.Log.Warning(ex, "MRT-ресурсы недоступны, локализация отключена.");
            _manager = null;
            _context = null;
        }
    }


    public static string T(string key)
    {
        return Cache.GetOrAdd(key, static k =>
        {
            try
            {
                var manager = _manager ?? GetManager();
                var context = _context ?? manager.CreateResourceContext();
                var value = manager.MainResourceMap.GetValue("Resources/" + k, context)?.ValueAsString;
                return string.IsNullOrEmpty(value) ? k : value;
            }
            catch
            {
                return k;
            }
        });
    }

    private static ResourceManager GetManager() => _manager ??= new ResourceManager();
}

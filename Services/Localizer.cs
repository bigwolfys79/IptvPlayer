using System.Xml.Linq;

namespace IptvPlayer.Services;


// Runtime string resolution reads Strings/<lang>/Resources.resw directly:
// MRT Core resource-context language override is broken in unpackaged builds (0x80073B17)
public static class L
{
    private static System.Collections.Generic.IDictionary<string, string>? _table;
    public static string Lang { get; private set; } = "ru";

    public static bool IsRussian => Lang != "en";


    public static void SetLanguage(string lang)
    {
        Lang = string.IsNullOrEmpty(lang) ? "ru" : lang;
        try
        {
            // Affects x:Uid resolution of pages loaded after the switch
            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = Lang == "en" ? "en-US" : "ru-RU";
        }
        catch (System.Exception ex)
        {
            // Unpackaged builds reject mid-session overrides; language still applies
            // via L.T + page reload, x:Uid resources update after app restart
            Serilog.Log.Debug(ex,
                "PrimaryLanguageOverride не применился — язык применён через L.T, x:Uid-ресурсы обновятся после перезапуска.");
        }
        _table = LoadResw(PathFor(Lang));
    }


    public static string T(string key)
    {
        if (_table is null)
            _table = LoadResw(PathFor(Lang));
        if (_table is not null && _table.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value))
            return value;
        return key;
    }

    private static string PathFor(string lang) =>
        System.IO.Path.Combine(AppContext.BaseDirectory, "Strings", lang == "en" ? "en-US" : "ru-RU", "Resources.resw");

    private static System.Collections.Generic.IDictionary<string, string>? LoadResw(string path)
    {
        try
        {
            var root = XDocument.Load(path).Root ?? throw new System.InvalidOperationException("empty resw");
            var dict = new System.Collections.Generic.Dictionary<string, string>(System.StringComparer.Ordinal);
            foreach (var data in root.Elements("data"))
            {
                var name = (string?)data.Attribute("name");
                var value = (string?)data.Element("value");
                if (!string.IsNullOrEmpty(name) && value is not null)
                    dict[name] = value;
            }
            return dict;
        }
        catch (System.Exception ex)
        {
            Serilog.Log.Warning(ex, "Resw не прочитан: {Path}", path);
            return null;
        }
    }
}

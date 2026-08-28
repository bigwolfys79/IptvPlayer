using Microsoft.Windows.ApplicationModel.Resources;

namespace IptvPlayer.Services;

/// <summary>
/// Точка входа в локализацию: все пользовательские строки живут в
/// Strings/ru-RU/Resources.resw и Strings/en-US/Resources.resw, в коде —
/// L.T("KeyName"), в XAML — x:Uid="KeyName" со значениями вида
/// "KeyName.Property" в resw.
///
/// Язык выбирается один раз при старте (App читает настройку до создания
/// окна) через qualifier "language" у MRT-контекста — поэтому смена языка
/// в настройках применяется после перезапуска. Если ключ в resw не найден,
/// возвращается сам ключ (легко заметить недостающий перевод).
/// Файловый лог намеренно остаётся русским (он для разработчика).
/// </summary>
public static class L
{
    private static ResourceManager? _manager;
    private static ResourceContext? _context;

    public static string Lang { get; private set; } = "ru";

    public static bool IsRussian => Lang != "en";

    /// <summary>
    /// Выбирает язык для MRT. Должен вызываться ДО создания XAML-окна
    /// (App.OnLaunched/конструктор App): x:Uid-тексты применяются при
    /// разборе XAML и на лету не меняются.
    /// </summary>
    public static void SetLanguage(string lang)
    {
        Lang = string.IsNullOrEmpty(lang) ? "ru" : lang;
        try
        {
            _context = GetManager().CreateResourceContext();
            _context.QualifierValues["language"] = Lang == "en" ? "en-US" : "ru-RU";
        }
        catch (System.Exception ex)
        {
            // Нет resources.pri рядом с exe (кривая сборка/запуск из-под
            // другой папки) — MRT недоступен, T() будет возвращать ключи.
            Serilog.Log.Warning(ex, "MRT-ресурсы недоступны, локализация отключена.");
            _manager = null;
            _context = null;
        }
    }

    /// <summary>Возвращает строку ключа на текущем языке (или сам ключ).</summary>
    public static string T(string key)
    {
        try
        {
            var manager = _manager ?? GetManager();
            var context = _context ?? manager.CreateResourceContext();
            var value = manager.MainResourceMap.GetValue("Resources/" + key, context)?.ValueAsString;
            return string.IsNullOrEmpty(value) ? key : value;
        }
        catch
        {
            return key;
        }
    }

    private static ResourceManager GetManager() => _manager ??= new ResourceManager();
}

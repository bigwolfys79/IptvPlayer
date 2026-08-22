namespace IptvPlayer.Services;

/// <summary>
/// Минималистичный локализатор: строки задаются парами прямо в месте
/// использования — L.T("Каналы", "Channels") — без внешних файлов ресурсов.
/// Русский — язык по умолчанию; при Lang = "en" возвращается английский
/// вариант. Файловый лог намеренно остаётся русским (он для разработчика).
///
/// Текст применяется: в коде, собирающем диалоги/сообщения, в момент
/// построения (диалог настроек пересобирается при каждом открытии), а
/// статичные элементы XAML переводятся методом ApplyLanguage в MainPage
/// (элементы имеют x:Name).
/// </summary>
public static class L
{
    public static string Lang { get; private set; } = "ru";

    public static bool IsRussian => Lang != "en";

    public static void SetLanguage(string lang)
    {
        Lang = string.IsNullOrEmpty(lang) ? "ru" : lang;
    }

    /// <summary>Возвращает строку на текущем языке.</summary>
    public static string T(string ru, string en)
    {
        return Lang == "en" ? en : ru;
    }
}

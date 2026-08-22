namespace IptvPlayer.Models;

/// <summary>
/// Сохранённое состояние окна (физические пиксели, как отдаёт AppWindow —
/// на одном и том же мониторе сохранение/восстановление консистентно).
/// При восстановлении координаты вписываются в рабочую область ближайшего
/// монитора, чтобы окно не оказалось за экраном (например, после
/// отключения второго монитора).
/// </summary>
public class WindowPlacement
{
    public int Left { get; set; }

    public int Top { get; set; }

    public int Width { get; set; }

    public int Height { get; set; }

    public bool Maximized { get; set; }
}

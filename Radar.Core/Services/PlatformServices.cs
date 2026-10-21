namespace Radar.Services;

/// <summary>
/// Сервисы мобильной ОС, которых нет в общей части: системные уведомления и звук, фоновая служба опроса,
/// открытие каталога данных. На десктопе не задаются — там работают встроенные способы (notify-send, плееры).
/// </summary>
public interface IPlatformServices
{
    /// <summary>Системное уведомление (канал тревог).</summary>
    void Notify(string title, string body, bool urgent);
    /// <summary>Проиграть синтезированный сигнал (WAV-файл).</summary>
    void PlaySound(string wavPath);
    /// <summary>Фоновая служба: опрос каналов и уведомления, пока приложение свёрнуто.</summary>
    void SetBackgroundService(bool on);
    /// <summary>Каталог данных приложения (кеши, настройки).</summary>
    string DataDirectory { get; }
    /// <summary>Внешний экран (трансляция, HDMI), на который выведена карта: «имя · ширина×высота» или null.</summary>
    string? ExternalScreen { get; }
    /// <summary>Подключение или отключение внешнего экрана.</summary>
    event Action? ExternalScreenChanged;
    /// <summary>Системный экран трансляции: выбор телевизора или монитора в сети.</summary>
    void OpenCastSettings();
}

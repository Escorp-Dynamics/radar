using Radar.Models;

namespace Radar.Services;

/// <summary>
/// Дальность поражения по типу предполагаемого вооружения (км): сверху ограничивает веер возможных направлений пуска
/// и поиск места пуска по треку. Значения — округлённые рабочие максимумы по открытым данным о характеристиках,
/// а не паспортные рекорды: для оценки «может ли оттуда достать» нужен реалистичный потолок.
/// </summary>
public static class LaunchRange
{
    /// <summary>Дальность без уточнения типа — осторожная: ствольная и реактивная артиллерия не «достаёт» через всю страну.</summary>
    public const double UnknownKm = 100;

    /// <summary>Максимальная дальность средства, км. Тип неизвестен — по группе угроз.</summary>
    public static double MaxKm(ThreatKind kind, ThreatType group) => kind switch
    {
        // наземный огонь
        ThreatKind.Mlrs => 70,                 // «Град» ~20–40, «Ураган» ~35–70, «Смерч» ~70–90: берём верх для общей РСЗО
        ThreatKind.Artillery => 40,
        ThreatKind.Mines => 40,
        // реактивные снаряды РСЗО западного образца
        ThreatKind.MissileMlrs => 90,          // GMLRS ~70–90; ATACMS идёт отдельным типом
        // баллистика и ракеты
        ThreatKind.MissileBallistic => 300,    // ATACMS до 300, «Точка-У» ~120, «Сапсан» ~480 (редко)
        ThreatKind.MissileCruise => 560,       // Storm Shadow / SCALP ~560, «Фламинго» заявлен значительно дальше, но на практике дальность не подтверждена
        ThreatKind.MissileAntiShip => 300,     // «Нептун» (наземный пуск) ~280–300
        ThreatKind.MissileAntiRadar => 150,    // HARM / AGM-88 ~150
        ThreatKind.MissileGeneric => 500,
        // авиация
        ThreatKind.GuidedBomb => 90,           // планирующие УМПК/JDAM-ER ~60–80
        ThreatKind.Aircraft or ThreatKind.AviationGeneric or ThreatKind.Helicopter => 450,
        // БПЛА
        ThreatKind.UavFpv or ThreatKind.UavFiber => 25,
        ThreatKind.UavHeavy => 20,
        ThreatKind.UavHornet or ThreatKind.UavRecon => 120,
        ThreatKind.UavStrike or ThreatKind.UavJet => 1500, // дальние дроны самолётного типа
        ThreatKind.UavGeneric or ThreatKind.UavCarrier => 1000,
        ThreatKind.Usv => 600,
        ThreatKind.Uuv => 300,
        _ => GroupKm(group),
    };

    /// <summary>Тип не определён — по группе угроз.</summary>
    public static double GroupKm(ThreatType t) => t switch
    {
        ThreatType.Uav => 1000,
        ThreatType.Missile => 500,
        ThreatType.Aviation => 450,
        ThreatType.Artillery => 70,
        ThreatType.Naval => 600,
        _ => UnknownKm,
    };

    /// <summary>
    /// Дальность для события, у которого может быть несколько названных типов средств: берётся наибольшая из названных
    /// (в тексте «ракета или РСЗО» цель может достать и дальняя), а не наименьшая.
    /// </summary>
    public static double MaxKm(ThreatKind kind, IReadOnlyList<ThreatKind> kinds, ThreatType group)
    {
        double best = kind != ThreatKind.Unknown ? MaxKm(kind, group) : 0;
        foreach (var k in kinds) best = Math.Max(best, MaxKm(k, group));
        return best > 0 ? best : GroupKm(group);
    }
}

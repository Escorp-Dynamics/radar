using System.Text.RegularExpressions;

namespace Radar.Models;

/// <summary>Конкретный тип средства поражения внутри группы угроз.</summary>
public enum ThreatKind
{
    Unknown = 0,

    // БПЛА
    UavGeneric,      // БПЛА без уточнения
    UavStrike,       // ударный самолётного типа (Лютый, Бобр, FP-1, «Дартс», «Шарк» и т. п.)
    UavJet,          // реактивный БПЛА
    UavFpv,          // FPV-дрон
    UavFiber,        // FPV на оптоволокне, «ждун»
    UavHeavy,        // тяжёлый мультикоптер-бомбардировщик («Баба-яга», «Вампир»)
    UavCarrier,      // носитель/ретранслятор («матка», «крыло»)
    UavHornet,       // «Хорнет» — малый ударный самолётного типа
    UavRecon,        // разведчик

    // Ракеты
    MissileGeneric,
    MissileCruise,   // крылатые (Storm Shadow/SCALP, «Фламинго»)
    MissileAntiShip, // ПКР («Нептун», Harpoon)
    MissileBallistic,// баллистические (ATACMS, «Гром», «Сапсан», «Точка»)
    MissileMlrs,     // реактивные снаряды HIMARS/GMLRS
    MissileAntiRadar,// ПРР

    // Авиация
    AviationGeneric, // авиационная (ракетно-бомбовая) опасность
    GuidedBomb,      // УАБ / КАБ / JDAM / Hammer
    Aircraft,        // самолёты противника в воздухе (F-16, Су-24, «Мираж»)
    Helicopter,

    // Флот
    Usv,             // безэкипажный катер
    Uuv,             // подводный дрон

    // Наземный огонь
    Mlrs,            // РСЗО («Град», «Ураган», «Вампир»)
    Artillery,
    Mines,           // дистанционное минирование, «лепестки»

    // Прочее
    Balloon,         // аэростаты, МРШ / МВШ
    Sabotage,        // ДРГ
}

public enum AlertLevel
{
    /// <summary>«Внимание» — возможна угроза, усилить бдительность.</summary>
    Attention,
    /// <summary>«Опасность» — угроза пусков/приближения.</summary>
    Danger,
    /// <summary>«Тревога» — уже летит.</summary>
    Alarm,
    /// <summary>«Удар» — прилёты, работа ПВО.</summary>
    Strike,
}

public sealed record ThreatInfo(ThreatKind Kind, ThreatType Group, string Name, string Short, string Color, string Icon, double SpeedKmh);

/// <summary>Справочник угроз: распознавание, названия, иконки и оттенки цвета внутри группы.</summary>
public static class ThreatCatalog
{
    // Оттенки: внутри группы — одна гамма. БПЛА — оранжевые, ракеты — красные/малиновые,
    // авиация — фиолетовые, флот — голубые, наземный огонь — жёлтые, аэростаты — бирюзовые, ДРГ — серые.
    public static readonly IReadOnlyDictionary<ThreatKind, ThreatInfo> All = new Dictionary<ThreatKind, ThreatInfo>
    {
        [ThreatKind.UavGeneric] = new(ThreatKind.UavGeneric, ThreatType.Uav, "БПЛА", "БПЛА", "#F28C38", "uav", 170),
        [ThreatKind.UavStrike] = new(ThreatKind.UavStrike, ThreatType.Uav, "Ударный БПЛА", "Ударный", "#F07A2A", "uav_strike", 180),
        [ThreatKind.UavJet] = new(ThreatKind.UavJet, ThreatType.Uav, "Реактивный БПЛА", "Реактивный", "#FF5F1F", "uav_jet", 450),
        [ThreatKind.UavHornet] = new(ThreatKind.UavHornet, ThreatType.Uav, "БПЛА «Хорнет»", "Хорнет", "#F5A04A", "uav_small", 120),
        [ThreatKind.UavFpv] = new(ThreatKind.UavFpv, ThreatType.Uav, "FPV-дрон", "FPV", "#F7B55C", "fpv", 80),
        [ThreatKind.UavFiber] = new(ThreatKind.UavFiber, ThreatType.Uav, "FPV на оптоволокне", "FPV-оптика", "#E8A14F", "fpv_fiber", 60),
        [ThreatKind.UavHeavy] = new(ThreatKind.UavHeavy, ThreatType.Uav, "Тяжёлый коптер («Баба-яга»)", "Баба-яга", "#D9822B", "heavy_copter", 60),
        [ThreatKind.UavCarrier] = new(ThreatKind.UavCarrier, ThreatType.Uav, "Носитель FPV / «матка»", "Носитель", "#C9772C", "uav_carrier", 120),
        [ThreatKind.UavRecon] = new(ThreatKind.UavRecon, ThreatType.Uav, "БПЛА-разведчик", "Разведчик", "#E3B07A", "uav_recon", 120),

        [ThreatKind.MissileGeneric] = new(ThreatKind.MissileGeneric, ThreatType.Missile, "Ракета", "Ракета", "#E5484D", "missile", 850),
        [ThreatKind.MissileCruise] = new(ThreatKind.MissileCruise, ThreatType.Missile, "Крылатая ракета", "КР", "#F2555A", "cruise", 850),
        [ThreatKind.MissileAntiShip] = new(ThreatKind.MissileAntiShip, ThreatType.Missile, "ПКР «Нептун»", "ПКР", "#FF6B6B", "antiship", 900),
        [ThreatKind.MissileBallistic] = new(ThreatKind.MissileBallistic, ThreatType.Missile, "Баллистическая ракета", "Баллистика", "#C81E3A", "ballistic", 3500),
        [ThreatKind.MissileMlrs] = new(ThreatKind.MissileMlrs, ThreatType.Missile, "HIMARS / GMLRS", "HIMARS", "#D63F5C", "rocket", 2500),
        [ThreatKind.MissileAntiRadar] = new(ThreatKind.MissileAntiRadar, ThreatType.Missile, "Противорадиолокационная ракета", "ПРР", "#B8324E", "missile", 2000),

        [ThreatKind.AviationGeneric] = new(ThreatKind.AviationGeneric, ThreatType.Aviation, "Авиационная опасность", "Авиация", "#A87FE0", "aircraft", 700),
        [ThreatKind.GuidedBomb] = new(ThreatKind.GuidedBomb, ThreatType.Aviation, "Управляемая авиабомба (УАБ/КАБ)", "УАБ", "#9466D9", "bomb", 400),
        [ThreatKind.Aircraft] = new(ThreatKind.Aircraft, ThreatType.Aviation, "Самолёты противника", "Самолёты", "#B996EB", "aircraft", 750),
        [ThreatKind.Helicopter] = new(ThreatKind.Helicopter, ThreatType.Aviation, "Вертолёты противника", "Вертолёты", "#C7A9F0", "helicopter", 250),

        [ThreatKind.Usv] = new(ThreatKind.Usv, ThreatType.Naval, "Безэкипажный катер", "БЭК", "#3FA7D6", "usv", 55),
        [ThreatKind.Uuv] = new(ThreatKind.Uuv, ThreatType.Naval, "Подводный дрон", "Подв. дрон", "#2E86C1", "uuv", 20),

        [ThreatKind.Mlrs] = new(ThreatKind.Mlrs, ThreatType.Artillery, "РСЗО", "РСЗО", "#E2C044", "mlrs", 0),
        [ThreatKind.Artillery] = new(ThreatKind.Artillery, ThreatType.Artillery, "Артобстрел", "Обстрел", "#D4B03A", "artillery", 0),
        [ThreatKind.Mines] = new(ThreatKind.Mines, ThreatType.Artillery, "Дистанционное минирование", "Мины", "#C9A23A", "mines", 0),

        [ThreatKind.Balloon] = new(ThreatKind.Balloon, ThreatType.Balloon, "Аэростат / МРШ", "Аэростат", "#3CC9B4", "balloon", 40),
        [ThreatKind.Sabotage] = new(ThreatKind.Sabotage, ThreatType.Ground, "Диверсионная группа", "ДРГ", "#9AA5B4", "sabotage", 5),
    };

    /// <summary>Путь иконки (заполняется слоем отрисовки, чтобы модели не зависели от UI).</summary>
    public static Func<string, string> IconPathResolver { get; set; } = _ => "";
    public static string IconPath(string icon) => IconPathResolver(icon);

    public static ThreatInfo Info(ThreatKind k) => All.TryGetValue(k, out var i) ? i : GroupDefault(ThreatType.General);

    public static ThreatInfo GroupDefault(ThreatType t) => t switch
    {
        ThreatType.Uav => All[ThreatKind.UavGeneric],
        ThreatType.Missile => All[ThreatKind.MissileGeneric],
        ThreatType.Aviation => All[ThreatKind.AviationGeneric],
        ThreatType.Naval => All[ThreatKind.Usv],
        ThreatType.Artillery => All[ThreatKind.Artillery],
        ThreatType.Balloon => All[ThreatKind.Balloon],
        ThreatType.Ground => All[ThreatKind.Sabotage],
        _ => new ThreatInfo(ThreatKind.Unknown, ThreatType.General, "Опасность", "Опасность", "#D9A441", "alert", 170),
    };

    public static ThreatKind DefaultKind(ThreatType t) => t switch
    {
        ThreatType.Uav => ThreatKind.UavGeneric,
        ThreatType.Missile => ThreatKind.MissileGeneric,
        ThreatType.Aviation => ThreatKind.AviationGeneric,
        ThreatType.Naval => ThreatKind.Usv,
        ThreatType.Artillery => ThreatKind.Artillery,
        ThreatType.Balloon => ThreatKind.Balloon,
        ThreatType.Ground => ThreatKind.Sabotage,
        _ => ThreatKind.Unknown,
    };

    // Правила распознавания: порядок важен — более специфичные раньше.
    // Шаблоны применяются к нормализованному тексту (нижний регистр, ё→е).
    private static readonly (ThreatKind Kind, Regex Pattern)[] Rules =
    [
        (ThreatKind.Balloon, R(@"аэростат|\bм[рв]ш\b|воздушн\w* шар|малоразмерн\w* шар")),
        (ThreatKind.Sabotage, R(@"\bдрг\b|диверсионн")),
        (ThreatKind.Uuv, R(@"подводн\w* (?:дрон|аппарат|беспилот)|\bнпа\b")),
        (ThreatKind.Usv, R(@"\bбэк\b|безэкипажн|\bкатер(?:а|у|ом|е|ы|ов)?\b|морск\w* дрон|магура|sea baby")),

        (ThreatKind.MissileBallistic, R(@"баллист|atacms|атакмс|\bгром(?! среди)\b|грім|сапсан|точка-?у|искандер")),
        (ThreatKind.MissileMlrs, R(@"хаймарс|himars|gmlrs|\bм142\b")),
        (ThreatKind.MissileAntiShip, R(@"\bпкр\b|нептун|гарпун|harpoon|противокорабел")),
        (ThreatKind.MissileAntiRadar, R(@"\bпрр\b|противорадиолокац|harm\b")),
        (ThreatKind.MissileCruise, R(@"крылат|\bкр\b|\bкрвб\b|storm shadow|\bшторм(?:а|ы|ов|ами|ам|ом)?\b|кинжал|циркон|\bкалибр\w*|\bх-?101\b|\bх-?59\b|scalp|скальп|фламинго|flamingo|таурус|taurus")),

        (ThreatKind.GuidedBomb, R(@"\bуаб\w*|\bкаб(?:ы|ов|а|ами|ам|ах)?\b|\bкаб-?\d|авиабомб|jdam|hammer|хаммер|gbu-?\d|планирующ\w* бомб")),
        (ThreatKind.Helicopter, R(@"вертол[её]т")),
        (ThreatKind.Aircraft, R(@"самол[её]т|\bf-?16|\bф-?16|мираж|mirage|\bсу[ -]?(?:24|25|27|30|34|35)\b|\bмиг[ -]?(?:29|31)\b|\bту[ -]?(?:22|95|160)\b|\bf-?35\b|носител\w* (?:крылат|шторм|кр)")),

        // «в том числе реактивным» — у каналов это БПЛА; «реактивные снаряды/системы залпового огня», «реактивное бомбометание» — нет
        (ThreatKind.UavJet, R(@"реактивн\w* (?:бпла|беспилот|дрон)|бпла[^\n]{0,30}реактивн\w*(?! (?:снаряд|систем|бомбомет))|реактивн\w*(?: и [а-я]+)? бпла|с реактивн\w* ускорител|реактивн(?:ым|ые)\b(?! (?:снаряд|систем|бомбомет))")),
        (ThreatKind.UavFiber, R(@"оптоволок|на оптике|ждун")),
        (ThreatKind.UavHeavy, R(@"баб\w* яг|\bвампир\w*(?!\w| рсзо)|тяж[её]л\w* (?:коптер|дрон|бпла)|гексакоптер|октокоптер")),
        (ThreatKind.UavCarrier, R(@"\bматк\w*|носител\w* фпв|носител\w* fpv|ретранслятор|\bкрыло\b(?! (?:здани|дом|больниц|школ|самол[её]т|крыш))")),
        (ThreatKind.UavFpv, R(@"\bфпв\b|\bfpv\b|\bфвп\b|\bпфв\b|fpv-|фпв-")),
        (ThreatKind.UavHornet, R(@"хорнет|hornet")),
        (ThreatKind.UavRecon, R(@"разведчик|бпла-?разведк|разведк\w* бпла|\bмавик|mavic|\bлелека|\bфурия")),
        (ThreatKind.UavStrike, R(@"\bлют\w*\b|\bбобр\b|fp-?1\b|фп-?1\b|дартс|шарк\b|ударн\w* (?:бпла|беспилот|дрон)|камикадзе|самол[её]тн\w* типа|шахед|герань")),

        (ThreatKind.Mlrs, R(@"\bрсзо\b|систем\w* залпового огня|\bград(?:а|у|ом|е|ы|ов|ам|ами|ах)?\b(?! ?-)|ураган|смерч|вампир\w* рсзо")),
        (ThreatKind.Mines, R(@"(?<!раз)минирован|лепест|\bпфм\b|кассет")),
        (ThreatKind.Artillery, R(@"артилл|артобстрел|миномет|обстрел|\bарт[аыу]\b|\bартой\b")),
    ];

    private static Regex R(string p) => new(p, RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>Все типы угроз, упомянутые в тексте, в порядке специфичности.</summary>
    public static List<ThreatKind> Detect(string norm)
    {
        var list = new List<ThreatKind>();
        foreach (var (k, p) in Rules)
            if (p.IsMatch(norm) && !list.Contains(k)) list.Add(k);
        return list;
    }

    /// <summary>
    /// Основной тип для отображения: в группе — самый специфичный.
    /// Для «Реактивный БПЛА или ПКР Нептун» группа определяется парсером (БПЛА), здесь выбирается подтип этой группы.
    /// </summary>
    public static ThreatKind Primary(IReadOnlyList<ThreatKind> kinds, ThreatType group)
    {
        foreach (var k in kinds)
            if (Info(k).Group == group) return k;
        return DefaultKind(group);
    }

    /// <summary>Цвет категории «Пуск» (бейджи, отметка места пуска).</summary>
    public const string LaunchColor = "#FF7A59";
    public static string LaunchBadgeBg(AlertLevel l) => l == AlertLevel.Attention ? "#2A2220" : "#4A2418";

    public static AlertLevel DetectLevel(string norm, AlertStatus status)
    {
        if (status is AlertStatus.Impact or AlertStatus.AirDefense) return AlertLevel.Strike;
        // «возможна тревога/опасность» — лишь предупреждение, а не сама тревога
        if (Regex.IsMatch(norm, @"возможн\w* (?:тревог|опасност|угроз)")) return AlertLevel.Attention;
        // «нет угрозы», «угрозы нет» — отрицание, а не уровень: без другой лексемы уровня это лишь внимание
        var negated = Regex.Replace(norm, @"\bнет (?:угроз|опасност)\w*|(?:угроз|опасност)\w*[^\n.!]{0,20}\bнет\b", " ");
        if (negated != norm && !Regex.IsMatch(negated, @"\bтревог|опасност|угроз")) return AlertLevel.Attention;
        if (Regex.IsMatch(norm, @"\bтревог")) return AlertLevel.Alarm;
        if (Regex.IsMatch(norm, @"опасност|угроз")) return AlertLevel.Danger;
        if (Regex.IsMatch(norm, @"\bвнимани|возможн\w* подготовк|приготовиться|готовность")) return AlertLevel.Attention;
        return AlertLevel.Alarm;
    }

    /// <summary>Множитель «громкости» отрисовки уровня (заливка, пульс).</summary>
    public static double LevelIntensity(AlertLevel l) => l switch
    {
        AlertLevel.Attention => 0.45,
        AlertLevel.Danger => 0.72,
        AlertLevel.Strike => 1.3,
        _ => 1.0,
    };

    // Нейтральная (не конкурирующая с цветами типов) шкала плашек уровня: от серого к белому на красном.
    public static string LevelBadgeBg(AlertLevel l) => l switch
    {
        AlertLevel.Attention => "#1E242D",
        AlertLevel.Danger => "#3A3020",
        AlertLevel.Strike => "#D92D3F",
        _ => "#4A2228",
    };
    public static string LevelBadgeFg(AlertLevel l) => l switch
    {
        AlertLevel.Attention => "#9AA5B4",
        AlertLevel.Danger => "#F2C14E",
        AlertLevel.Strike => "#FFFFFF",
        _ => "#FF8A8F",
    };

    public static string LevelText(AlertLevel l) => l switch
    {
        AlertLevel.Attention => "Внимание",
        AlertLevel.Danger => "Опасность",
        AlertLevel.Strike => "Удар",
        _ => "Тревога",
    };
}

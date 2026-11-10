using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;

namespace Chaos.Domain.Capture;

public enum DurationUnit
{
    Minute,
    Hour,
    Day,
    Week,
    Month,
    Year,
}

/// <summary>A unit word: its unit and how many it stands for (Arabic duals such as ساعتين mean 2).</summary>
public readonly record struct UnitWord(DurationUnit Unit, int Count);

/// <summary>
/// Vocabulary for one quick-add language (PLAN §8: table-driven language packs). Words are
/// written naturally; the parser folds them exactly like the input (case, diacritics, Arabic
/// letter variants and digits), so "mañana", "غدًا" and "MANANA" all compare equal. Phrases
/// may contain spaces, which match any run of whitespace.
/// </summary>
public sealed record LanguagePack
{
    public required string Code { get; init; }
    public required string[] Today { get; init; }
    public required string[] Tomorrow { get; init; }
    public required string[] DayAfterTomorrow { get; init; }
    /// <summary>"in" before a duration: in 3 days, dans 3 jours, بعد 3 أيام.</summary>
    public required string[] InPrefix { get; init; }
    /// <summary>A marker after the duration instead: 3 hari lagi.</summary>
    public string[] InSuffix { get; init; } = [];
    public required IReadOnlyDictionary<string, UnitWord> Units { get; init; }
    public required IReadOnlyDictionary<string, DayOfWeek> Weekdays { get; init; }
    /// <summary>Short weekday forms, recognized only after a marker (next, due, every, on) so "sun cream" stays text.</summary>
    public IReadOnlyDictionary<string, DayOfWeek> WeekdayAbbreviations { get; init; } = new Dictionary<string, DayOfWeek>();
    public string[] NextPrefix { get; init; } = [];
    public string[] NextSuffix { get; init; } = [];
    /// <summary>Optional words before a date that add no meaning: on, le, el, this, يوم.</summary>
    public string[] OnMarkers { get; init; } = [];
    public required IReadOnlyDictionary<string, int> Months { get; init; }
    public string[] MonthJoiners { get; init; } = [];
    public string[] DaySuffixes { get; init; } = [];
    public required string[] DueMarkers { get; init; }
    public required string[] AtMarkers { get; init; }
    public string[] Am { get; init; } = [];
    public string[] Pm { get; init; } = [];
    /// <summary>Afternoon words that add 12 hours only to small hours (Indonesian siang: jam 2 siang = 14:00, jam 11 siang = 11:00).</summary>
    public string[] Midday { get; init; } = [];
    public string[] Noon { get; init; } = [];
    public string[] Midnight { get; init; } = [];
    public required string[] Every { get; init; }
    /// <summary>"every other week": interval 2.</summary>
    public string[] Other { get; init; } = [];
    public required string[] AfterDone { get; init; }
    public required string[] And { get; init; }
    public required string[] WeekdaysWord { get; init; }
    /// <summary>"every month on the 15th": the words before the day number.</summary>
    public string[] MonthDayMarkers { get; init; } = [];
    public required IReadOnlyDictionary<string, (RecurrenceFrequency Frequency, int Interval)> RepeatAdverbs { get; init; }
    public required string[] RemindMe { get; init; }
    public required string[] Before { get; init; }
    public required string[] EstimateMarkers { get; init; }
    public required string[] WaitingMarkers { get; init; }
    public required string[] FollowUpMarkers { get; init; }
    public required IReadOnlyDictionary<string, TaskPriority> Priorities { get; init; }
    public required IReadOnlyDictionary<string, int> NumberWords { get; init; }
    public required string[] PersonalSpace { get; init; }
    /// <summary>A unit without a number means one (Arabic: بعد ساعة, لمدة ساعة).</summary>
    public bool BareUnits { get; init; }
}

/// <summary>The six Phase 1 quick-add languages (PLAN §1 decision 2), in tie-break order.</summary>
public static class LanguagePacks
{
    private static Dictionary<string, UnitWord> Units(params (string Word, DurationUnit Unit, int Count)[] entries) =>
        entries.ToDictionary(entry => entry.Word, entry => new UnitWord(entry.Unit, entry.Count));

    private static Dictionary<string, DayOfWeek> Days(params (string Word, DayOfWeek Day)[] entries) =>
        entries.ToDictionary(entry => entry.Word, entry => entry.Day);

    private static Dictionary<string, int> Numbers(params (string Word, int Value)[] entries) =>
        entries.ToDictionary(entry => entry.Word, entry => entry.Value);

    private static Dictionary<string, TaskPriority> Priority(params (string Word, TaskPriority Value)[] entries) =>
        entries.ToDictionary(entry => entry.Word, entry => entry.Value);

    private static Dictionary<string, (RecurrenceFrequency, int)> Adverbs(params (string Word, RecurrenceFrequency Frequency, int Interval)[] entries) =>
        entries.ToDictionary(entry => entry.Word, entry => (entry.Frequency, entry.Interval));

    private const DurationUnit Min = DurationUnit.Minute, Hr = DurationUnit.Hour, Dy = DurationUnit.Day, Wk = DurationUnit.Week, Mo = DurationUnit.Month, Yr = DurationUnit.Year;
    private const DayOfWeek Mon = DayOfWeek.Monday, Tue = DayOfWeek.Tuesday, Wed = DayOfWeek.Wednesday, Thu = DayOfWeek.Thursday, Fri = DayOfWeek.Friday, Sat = DayOfWeek.Saturday, Sun = DayOfWeek.Sunday;
    private const RecurrenceFrequency FDay = RecurrenceFrequency.Day, FWeek = RecurrenceFrequency.Week, FMonth = RecurrenceFrequency.Month, FYear = RecurrenceFrequency.Year;

    public static readonly LanguagePack English = new()
    {
        Code = "en",
        Today = ["today", "tonight"],
        Tomorrow = ["tomorrow", "tmrw", "tmr"],
        DayAfterTomorrow = ["day after tomorrow", "the day after tomorrow"],
        InPrefix = ["in"],
        InSuffix = ["from now"],
        Units = Units(("minute", Min, 1), ("minutes", Min, 1), ("min", Min, 1), ("mins", Min, 1), ("m", Min, 1),
            ("hour", Hr, 1), ("hours", Hr, 1), ("hr", Hr, 1), ("hrs", Hr, 1), ("h", Hr, 1),
            ("day", Dy, 1), ("days", Dy, 1), ("week", Wk, 1), ("weeks", Wk, 1), ("wk", Wk, 1), ("wks", Wk, 1),
            ("month", Mo, 1), ("months", Mo, 1), ("year", Yr, 1), ("years", Yr, 1), ("yr", Yr, 1), ("yrs", Yr, 1)),
        Weekdays = Days(("monday", Mon), ("tuesday", Tue), ("wednesday", Wed), ("thursday", Thu), ("friday", Fri), ("saturday", Sat), ("sunday", Sun),
            ("mondays", Mon), ("tuesdays", Tue), ("wednesdays", Wed), ("thursdays", Thu), ("fridays", Fri), ("saturdays", Sat), ("sundays", Sun)),
        WeekdayAbbreviations = Days(("mon", Mon), ("tue", Tue), ("tues", Tue), ("wed", Wed), ("thu", Thu), ("thur", Thu), ("thurs", Thu), ("fri", Fri), ("sat", Sat), ("sun", Sun)),
        NextPrefix = ["next"],
        OnMarkers = ["on", "this"],
        Months = Numbers(("january", 1), ("february", 2), ("march", 3), ("april", 4), ("may", 5), ("june", 6), ("july", 7), ("august", 8), ("september", 9), ("october", 10), ("november", 11), ("december", 12),
            ("jan", 1), ("feb", 2), ("mar", 3), ("apr", 4), ("jun", 6), ("jul", 7), ("aug", 8), ("sep", 9), ("sept", 9), ("oct", 10), ("nov", 11), ("dec", 12)),
        MonthJoiners = ["of"],
        DaySuffixes = ["st", "nd", "rd", "th"],
        DueMarkers = ["by", "due", "due by", "due on", "before", "deadline"],
        AtMarkers = ["at"],
        Am = ["am", "a.m."],
        Pm = ["pm", "p.m."],
        Noon = ["noon", "midday"],
        Midnight = ["midnight"],
        Every = ["every", "each"],
        Other = ["other"],
        AfterDone = ["after done", "after completion", "after completing", "after it's done", "after finishing", "when done"],
        And = ["and", "&"],
        WeekdaysWord = ["weekday", "weekdays", "working day", "working days", "workday", "workdays", "work day", "work days"],
        MonthDayMarkers = ["on the", "on"],
        RepeatAdverbs = Adverbs(("daily", FDay, 1), ("weekly", FWeek, 1), ("fortnightly", FWeek, 2), ("biweekly", FWeek, 2), ("monthly", FMonth, 1), ("quarterly", FMonth, 3), ("yearly", FYear, 1), ("annually", FYear, 1)),
        RemindMe = ["remind me", "reminder", "remind"],
        Before = ["before", "earlier"],
        EstimateMarkers = ["for", "takes"],
        WaitingMarkers = ["waiting on", "waiting for"],
        FollowUpMarkers = ["follow up", "follow-up", "followup"],
        Priorities = Priority(("none", TaskPriority.None), ("low", TaskPriority.Low), ("normal", TaskPriority.Normal), ("medium", TaskPriority.Normal), ("high", TaskPriority.High), ("urgent", TaskPriority.Urgent)),
        NumberWords = Numbers(("a", 1), ("an", 1), ("one", 1), ("two", 2), ("three", 3), ("four", 4), ("five", 5), ("six", 6), ("seven", 7), ("eight", 8), ("nine", 9), ("ten", 10), ("twelve", 12)),
        PersonalSpace = ["personal"],
    };

    public static readonly LanguagePack French = new()
    {
        Code = "fr",
        Today = ["aujourd'hui", "aujourdhui", "ce soir"],
        Tomorrow = ["demain"],
        DayAfterTomorrow = ["après-demain", "après demain"],
        InPrefix = ["dans"],
        Units = Units(("minute", Min, 1), ("minutes", Min, 1), ("min", Min, 1), ("mn", Min, 1),
            ("heure", Hr, 1), ("heures", Hr, 1), ("h", Hr, 1), ("jour", Dy, 1), ("jours", Dy, 1), ("j", Dy, 1),
            ("semaine", Wk, 1), ("semaines", Wk, 1), ("mois", Mo, 1), ("an", Yr, 1), ("ans", Yr, 1), ("année", Yr, 1), ("années", Yr, 1)),
        Weekdays = Days(("lundi", Mon), ("mardi", Tue), ("mercredi", Wed), ("jeudi", Thu), ("vendredi", Fri), ("samedi", Sat), ("dimanche", Sun),
            ("lundis", Mon), ("mardis", Tue), ("mercredis", Wed), ("jeudis", Thu), ("vendredis", Fri), ("samedis", Sat), ("dimanches", Sun)),
        WeekdayAbbreviations = Days(("lun", Mon), ("mar", Tue), ("mer", Wed), ("jeu", Thu), ("ven", Fri), ("sam", Sat), ("dim", Sun)),
        NextPrefix = ["prochain"],
        NextSuffix = ["prochain"],
        OnMarkers = ["le", "ce"],
        Months = Numbers(("janvier", 1), ("février", 2), ("mars", 3), ("avril", 4), ("mai", 5), ("juin", 6), ("juillet", 7), ("août", 8), ("septembre", 9), ("octobre", 10), ("novembre", 11), ("décembre", 12),
            ("janv", 1), ("févr", 2), ("fév", 2), ("avr", 4), ("juil", 7), ("sept", 9), ("oct", 10), ("nov", 11), ("déc", 12)),
        DaySuffixes = ["er"],
        DueMarkers = ["avant", "d'ici", "au plus tard", "pour le", "échéance"],
        AtMarkers = ["à"],
        Am = ["du matin"],
        Pm = ["de l'après-midi", "de l'aprem", "du soir"],
        Noon = ["midi"],
        Midnight = ["minuit"],
        Every = ["chaque", "tous les", "toutes les"],
        AfterDone = ["après achèvement", "une fois terminé", "une fois terminée", "après avoir terminé", "après terminé", "une fois fait"],
        And = ["et"],
        WeekdaysWord = ["jours ouvrés", "jours ouvrables", "jour ouvré", "jours de semaine"],
        MonthDayMarkers = ["le"],
        RepeatAdverbs = Adverbs(("quotidien", FDay, 1), ("quotidiennement", FDay, 1), ("hebdomadaire", FWeek, 1), ("mensuel", FMonth, 1), ("mensuellement", FMonth, 1), ("trimestriel", FMonth, 3), ("annuel", FYear, 1), ("annuellement", FYear, 1)),
        RemindMe = ["rappelle-moi", "rappelle moi", "me rappeler", "rappel"],
        Before = ["avant", "à l'avance"],
        EstimateMarkers = ["pendant", "durée"],
        WaitingMarkers = ["en attente de", "attente de"],
        FollowUpMarkers = ["relancer", "relance", "suivi"],
        Priorities = Priority(("aucune", TaskPriority.None), ("basse", TaskPriority.Low), ("faible", TaskPriority.Low), ("normale", TaskPriority.Normal), ("haute", TaskPriority.High), ("élevée", TaskPriority.High), ("urgente", TaskPriority.Urgent)),
        NumberWords = Numbers(("un", 1), ("une", 1), ("deux", 2), ("trois", 3), ("quatre", 4), ("cinq", 5), ("six", 6), ("sept", 7), ("huit", 8), ("neuf", 9), ("dix", 10), ("douze", 12)),
        PersonalSpace = ["personnel", "perso"],
    };

    public static readonly LanguagePack Italian = new()
    {
        Code = "it",
        Today = ["oggi", "stasera"],
        Tomorrow = ["domani"],
        DayAfterTomorrow = ["dopodomani"],
        InPrefix = ["tra", "fra"],
        Units = Units(("minuto", Min, 1), ("minuti", Min, 1), ("min", Min, 1), ("ora", Hr, 1), ("ore", Hr, 1), ("h", Hr, 1),
            ("giorno", Dy, 1), ("giorni", Dy, 1), ("settimana", Wk, 1), ("settimane", Wk, 1), ("mese", Mo, 1), ("mesi", Mo, 1), ("anno", Yr, 1), ("anni", Yr, 1)),
        Weekdays = Days(("lunedì", Mon), ("martedì", Tue), ("mercoledì", Wed), ("giovedì", Thu), ("venerdì", Fri), ("sabato", Sat), ("domenica", Sun), ("sabati", Sat), ("domeniche", Sun)),
        WeekdayAbbreviations = Days(("lun", Mon), ("mar", Tue), ("mer", Wed), ("gio", Thu), ("ven", Fri), ("sab", Sat), ("dom", Sun)),
        NextPrefix = ["prossimo", "prossima", "il prossimo", "la prossima"],
        NextSuffix = ["prossimo", "prossima"],
        OnMarkers = ["il", "questo", "questa"],
        Months = Numbers(("gennaio", 1), ("febbraio", 2), ("marzo", 3), ("aprile", 4), ("maggio", 5), ("giugno", 6), ("luglio", 7), ("agosto", 8), ("settembre", 9), ("ottobre", 10), ("novembre", 11), ("dicembre", 12),
            ("gen", 1), ("feb", 2), ("mar", 3), ("apr", 4), ("mag", 5), ("giu", 6), ("lug", 7), ("ago", 8), ("set", 9), ("ott", 10), ("nov", 11), ("dic", 12)),
        DueMarkers = ["entro", "entro il", "scadenza", "scade", "prima di"],
        AtMarkers = ["alle", "alle ore", "all'", "ore", "a"],
        Am = ["di mattina", "del mattino", "di mattino"],
        Pm = ["del pomeriggio", "di pomeriggio", "di sera", "della sera"],
        Noon = ["mezzogiorno"],
        Midnight = ["mezzanotte"],
        Every = ["ogni", "tutti i", "tutte le"],
        AfterDone = ["dopo il completamento", "dopo completamento", "una volta completato", "una volta completata", "dopo averlo fatto", "dopo fatto"],
        And = ["e"],
        WeekdaysWord = ["giorni feriali", "giorno feriale", "giorni lavorativi", "giorno lavorativo"],
        MonthDayMarkers = ["il"],
        RepeatAdverbs = Adverbs(("quotidianamente", FDay, 1), ("giornalmente", FDay, 1), ("settimanalmente", FWeek, 1), ("mensilmente", FMonth, 1), ("trimestralmente", FMonth, 3), ("annualmente", FYear, 1)),
        RemindMe = ["ricordami", "ricordamelo", "promemoria"],
        Before = ["prima"],
        EstimateMarkers = ["per", "durata"],
        WaitingMarkers = ["in attesa di", "aspetto"],
        FollowUpMarkers = ["sollecitare", "sollecito", "ricontattare"],
        Priorities = Priority(("nessuna", TaskPriority.None), ("bassa", TaskPriority.Low), ("normale", TaskPriority.Normal), ("media", TaskPriority.Normal), ("alta", TaskPriority.High), ("urgente", TaskPriority.Urgent)),
        NumberWords = Numbers(("un", 1), ("uno", 1), ("una", 1), ("due", 2), ("tre", 3), ("quattro", 4), ("cinque", 5), ("sei", 6), ("sette", 7), ("otto", 8), ("nove", 9), ("dieci", 10), ("dodici", 12)),
        PersonalSpace = ["personale"],
    };

    public static readonly LanguagePack Spanish = new()
    {
        Code = "es",
        Today = ["hoy", "esta noche"],
        Tomorrow = ["mañana"],
        DayAfterTomorrow = ["pasado mañana"],
        InPrefix = ["en", "dentro de"],
        Units = Units(("minuto", Min, 1), ("minutos", Min, 1), ("min", Min, 1), ("hora", Hr, 1), ("horas", Hr, 1), ("h", Hr, 1),
            ("día", Dy, 1), ("días", Dy, 1), ("semana", Wk, 1), ("semanas", Wk, 1), ("mes", Mo, 1), ("meses", Mo, 1), ("año", Yr, 1), ("años", Yr, 1)),
        Weekdays = Days(("lunes", Mon), ("martes", Tue), ("miércoles", Wed), ("jueves", Thu), ("viernes", Fri), ("sábado", Sat), ("domingo", Sun), ("sábados", Sat), ("domingos", Sun)),
        WeekdayAbbreviations = Days(("lun", Mon), ("mar", Tue), ("mié", Wed), ("jue", Thu), ("vie", Fri), ("sáb", Sat), ("dom", Sun)),
        NextPrefix = ["el próximo", "próximo", "la próxima", "próxima"],
        NextSuffix = ["próximo", "que viene"],
        OnMarkers = ["el", "este"],
        Months = Numbers(("enero", 1), ("febrero", 2), ("marzo", 3), ("abril", 4), ("mayo", 5), ("junio", 6), ("julio", 7), ("agosto", 8), ("septiembre", 9), ("setiembre", 9), ("octubre", 10), ("noviembre", 11), ("diciembre", 12),
            ("ene", 1), ("feb", 2), ("mar", 3), ("abr", 4), ("jun", 6), ("jul", 7), ("ago", 8), ("sep", 9), ("sept", 9), ("oct", 10), ("nov", 11), ("dic", 12)),
        MonthJoiners = ["de"],
        DueMarkers = ["para", "para el", "antes del", "antes de", "vence", "vence el", "fecha límite"],
        AtMarkers = ["a las", "a la"],
        Am = ["de la mañana", "am"],
        Pm = ["de la tarde", "de la noche", "pm"],
        Noon = ["mediodía"],
        Midnight = ["medianoche"],
        Every = ["cada", "todos los", "todas las"],
        AfterDone = ["después de completar", "después de completarla", "tras completar", "al completar", "después de terminar", "una vez hecho"],
        And = ["y"],
        WeekdaysWord = ["días laborables", "día laborable", "días hábiles", "entre semana"],
        MonthDayMarkers = ["el día", "el"],
        RepeatAdverbs = Adverbs(("diario", FDay, 1), ("diariamente", FDay, 1), ("semanal", FWeek, 1), ("semanalmente", FWeek, 1), ("quincenal", FWeek, 2), ("mensual", FMonth, 1), ("mensualmente", FMonth, 1), ("trimestral", FMonth, 3), ("anual", FYear, 1), ("anualmente", FYear, 1)),
        RemindMe = ["recuérdame", "recordarme", "recordatorio", "avísame"],
        Before = ["antes"],
        EstimateMarkers = ["durante", "por", "dura"],
        WaitingMarkers = ["esperando a", "a la espera de", "esperando"],
        FollowUpMarkers = ["dar seguimiento", "hacer seguimiento", "seguimiento"],
        Priorities = Priority(("ninguna", TaskPriority.None), ("baja", TaskPriority.Low), ("normal", TaskPriority.Normal), ("media", TaskPriority.Normal), ("alta", TaskPriority.High), ("urgente", TaskPriority.Urgent)),
        NumberWords = Numbers(("un", 1), ("una", 1), ("uno", 1), ("dos", 2), ("tres", 3), ("cuatro", 4), ("cinco", 5), ("seis", 6), ("siete", 7), ("ocho", 8), ("nueve", 9), ("diez", 10), ("doce", 12)),
        PersonalSpace = ["personal"],
    };

    public static readonly LanguagePack Indonesian = new()
    {
        Code = "id",
        Today = ["hari ini", "malam ini", "nanti malam"],
        Tomorrow = ["besok", "esok"],
        DayAfterTomorrow = ["lusa"],
        InPrefix = ["dalam"],
        InSuffix = ["lagi"],
        // "minggu" is a week here; Sunday is "hari Minggu" or "Ahad" so "setiap minggu" stays weekly.
        Units = Units(("menit", Min, 1), ("mnt", Min, 1), ("jam", Hr, 1), ("hari", Dy, 1), ("minggu", Wk, 1), ("pekan", Wk, 1), ("bulan", Mo, 1), ("tahun", Yr, 1)),
        Weekdays = Days(("senin", Mon), ("selasa", Tue), ("rabu", Wed), ("kamis", Thu), ("jumat", Fri), ("jum'at", Fri), ("sabtu", Sat), ("ahad", Sun), ("hari minggu", Sun),
            ("hari senin", Mon), ("hari selasa", Tue), ("hari rabu", Wed), ("hari kamis", Thu), ("hari jumat", Fri), ("hari sabtu", Sat)),
        NextPrefix = [],
        NextSuffix = ["depan"],
        OnMarkers = ["pada", "pada hari"],
        Months = Numbers(("januari", 1), ("februari", 2), ("maret", 3), ("april", 4), ("mei", 5), ("juni", 6), ("juli", 7), ("agustus", 8), ("september", 9), ("oktober", 10), ("november", 11), ("desember", 12),
            ("jan", 1), ("feb", 2), ("mar", 3), ("apr", 4), ("jun", 6), ("jul", 7), ("agu", 8), ("agt", 8), ("sep", 9), ("okt", 10), ("nov", 11), ("des", 12)),
        DueMarkers = ["sebelum", "paling lambat", "tenggat", "batas waktu", "deadline"],
        AtMarkers = ["jam", "pukul", "pada pukul", "pada jam"],
        Am = ["pagi"],
        Pm = ["sore", "malam"],
        Midday = ["siang"],
        Noon = ["tengah hari"],
        Midnight = ["tengah malam"],
        Every = ["setiap", "tiap", "tiap-tiap"],
        AfterDone = ["setelah selesai", "sesudah selesai", "setelah dikerjakan"],
        And = ["dan"],
        WeekdaysWord = ["hari kerja"],
        MonthDayMarkers = ["tanggal", "pada tanggal"],
        RepeatAdverbs = Adverbs(("harian", FDay, 1), ("mingguan", FWeek, 1), ("bulanan", FMonth, 1), ("tahunan", FYear, 1)),
        RemindMe = ["ingatkan saya", "ingatkan aku", "ingatkan", "pengingat"],
        Before = ["sebelumnya", "sebelum"],
        EstimateMarkers = ["selama", "durasi"],
        WaitingMarkers = ["menunggu", "tunggu"],
        FollowUpMarkers = ["tindak lanjut", "tindaklanjuti", "follow up"],
        Priorities = Priority(("rendah", TaskPriority.Low), ("normal", TaskPriority.Normal), ("sedang", TaskPriority.Normal), ("tinggi", TaskPriority.High), ("penting", TaskPriority.High), ("mendesak", TaskPriority.Urgent)),
        NumberWords = Numbers(("satu", 1), ("se", 1), ("dua", 2), ("tiga", 3), ("empat", 4), ("lima", 5), ("enam", 6), ("tujuh", 7), ("delapan", 8), ("sembilan", 9), ("sepuluh", 10), ("dua belas", 12)),
        PersonalSpace = ["pribadi"],
    };

    public static readonly LanguagePack Arabic = new()
    {
        Code = "ar",
        Today = ["اليوم", "الليلة"],
        Tomorrow = ["غدًا", "غدا", "بكرة", "بكره"],
        DayAfterTomorrow = ["بعد غد", "بعد الغد", "بعد بكرة"],
        InPrefix = ["بعد", "خلال"],
        Units = Units(("دقيقة", Min, 1), ("دقائق", Min, 1), ("دقيقتين", Min, 2), ("دقيقتان", Min, 2),
            ("ساعة", Hr, 1), ("ساعات", Hr, 1), ("ساعتين", Hr, 2), ("ساعتان", Hr, 2),
            ("يوم", Dy, 1), ("أيام", Dy, 1), ("يومين", Dy, 2), ("يومان", Dy, 2),
            ("أسبوع", Wk, 1), ("أسابيع", Wk, 1), ("أسبوعين", Wk, 2), ("أسبوعان", Wk, 2),
            ("شهر", Mo, 1), ("أشهر", Mo, 1), ("شهور", Mo, 1), ("شهرين", Mo, 2), ("شهران", Mo, 2),
            ("سنة", Yr, 1), ("سنوات", Yr, 1), ("عام", Yr, 1), ("أعوام", Yr, 1), ("سنتين", Yr, 2), ("عامين", Yr, 2)),
        Weekdays = Days(("الأحد", Sun), ("الاثنين", Mon), ("الإثنين", Mon), ("الثلاثاء", Tue), ("الأربعاء", Wed), ("الخميس", Thu), ("الجمعة", Fri), ("السبت", Sat),
            ("يوم الأحد", Sun), ("يوم الاثنين", Mon), ("يوم الثلاثاء", Tue), ("يوم الأربعاء", Wed), ("يوم الخميس", Thu), ("يوم الجمعة", Fri), ("يوم السبت", Sat)),
        NextSuffix = ["القادم", "المقبل", "الجاي"],
        OnMarkers = ["في"],
        Months = Numbers(("يناير", 1), ("فبراير", 2), ("مارس", 3), ("أبريل", 4), ("ابريل", 4), ("مايو", 5), ("يونيو", 6), ("يوليو", 7), ("أغسطس", 8), ("سبتمبر", 9), ("أكتوبر", 10), ("نوفمبر", 11), ("ديسمبر", 12),
            ("كانون الثاني", 1), ("شباط", 2), ("آذار", 3), ("نيسان", 4), ("أيار", 5), ("حزيران", 6), ("تموز", 7), ("آب", 8), ("أيلول", 9), ("تشرين الأول", 10), ("تشرين الثاني", 11), ("كانون الأول", 12)),
        DueMarkers = ["قبل", "بحلول", "موعد التسليم", "آخر موعد", "الموعد النهائي"],
        AtMarkers = ["الساعة", "في الساعة", "عند الساعة", "على الساعة"],
        Am = ["ص", "صباحًا", "صباحا", "الصبح"],
        Pm = ["م", "مساءً", "مساء", "مساءا", "العصر", "بعد الظهر", "ليلًا", "ليلا"],
        Noon = ["الظهر", "ظهرًا", "ظهرا"],
        Midnight = ["منتصف الليل"],
        Every = ["كل"],
        AfterDone = ["بعد الانتهاء", "بعد الإنجاز", "بعد الاكتمال", "بعد الإكمال", "بعد إتمامها"],
        And = ["و"],
        WeekdaysWord = ["أيام العمل", "يوم عمل"],
        MonthDayMarkers = ["يوم"],
        RepeatAdverbs = Adverbs(("يوميًا", FDay, 1), ("يوميا", FDay, 1), ("أسبوعيًا", FWeek, 1), ("أسبوعيا", FWeek, 1), ("شهريًا", FMonth, 1), ("شهريا", FMonth, 1), ("سنويًا", FYear, 1), ("سنويا", FYear, 1)),
        RemindMe = ["ذكرني", "ذكّرني", "نبهني", "تذكير"],
        Before = ["قبل", "قبلها"],
        EstimateMarkers = ["لمدة", "مدة", "يستغرق"],
        WaitingMarkers = ["بانتظار", "في انتظار", "انتظار"],
        FollowUpMarkers = ["متابعة", "المتابعة", "تابع"],
        Priorities = Priority(("منخفضة", TaskPriority.Low), ("عادية", TaskPriority.Normal), ("متوسطة", TaskPriority.Normal), ("عالية", TaskPriority.High), ("مهمة", TaskPriority.High), ("عاجلة", TaskPriority.Urgent), ("عاجل", TaskPriority.Urgent)),
        NumberWords = Numbers(("واحد", 1), ("اثنين", 2), ("اثنان", 2), ("ثلاثة", 3), ("ثلاث", 3), ("أربعة", 4), ("أربع", 4), ("خمسة", 5), ("خمس", 5), ("ستة", 6), ("ست", 6), ("سبعة", 7), ("سبع", 7), ("ثمانية", 8), ("ثماني", 8), ("تسعة", 9), ("تسع", 9), ("عشرة", 10), ("عشر", 10)),
        PersonalSpace = ["شخصي"],
        BareUnits = true,
    };

    public static readonly IReadOnlyList<LanguagePack> All = [English, French, Italian, Spanish, Indonesian, Arabic];
}

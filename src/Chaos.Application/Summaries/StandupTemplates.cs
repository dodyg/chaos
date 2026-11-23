namespace Chaos.Application.Summaries;

/// <summary>
/// The headings of a prefilled standup (PLAN3 9.12) in the six quick-add languages, chosen by the
/// primary subtag of the profile's locale as the nudge text is; any other language gets English.
/// The person edits the whole text before posting it.
/// </summary>
public static class StandupTemplates
{
    public static (string Done, string Planned, string Blocked) Headings(string? locale)
    {
        var primary = (locale ?? string.Empty).Split('-', '_')[0].Trim().ToLowerInvariant();
        return primary switch
        {
            "fr" => ("Fait", "Prévu", "Bloqué"),
            "it" => ("Fatto", "In programma", "Bloccato"),
            "es" => ("Hecho", "Planeado", "Bloqueado"),
            "id" => ("Selesai", "Direncanakan", "Terhambat"),
            "ar" => ("منجز", "مخطط", "متعثر"),
            _ => ("Done", "Planned", "Blocked"),
        };
    }
}

using Chaos.Domain.Capture;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tasks;

namespace Chaos.Domain.Tests.Capture;

/// <summary>
/// Quick-add behavior (PLAN §8, §16 Capture row). The reference moment is Tuesday
/// 10 November 2026 09:00 UTC with Monday as the first weekday and day/month numeric dates.
/// </summary>
public sealed class QuickAddParserTests
{
    private static readonly DateOnly Today = new(2026, 11, 10);

    private static QuickAddContext Context(DateTimeOffset? now = null, string zone = "UTC", string format = "DMY", IReadOnlyList<CaptureSpan>? excluded = null) => new(
        now ?? new DateTimeOffset(2026, 11, 10, 9, 0, 0, TimeSpan.Zero), zone, format, DayOfWeek.Monday, new TimeOnly(9, 0),
        [new("list-reading", "Reading"), new("list-book", "Book Club"), new("errands-1", "Errands"), new("errands-2", "errands")],
        ["work", "home/garden"],
        excluded ?? []);

    private static QuickAddResult Parse(string text, QuickAddContext? context = null) => QuickAddParser.Parse(text, context ?? Context());

    [Test]
    public async Task Parse_CategorySigil_SetsWorkOrPersonalAndLeavesTheTitle()
    {
        var work = Parse("Ship release ~work tomorrow");
        await Assert.That(work.Draft.Title).IsEqualTo("Ship release");
        await Assert.That(work.Draft.Category).IsEqualTo(CapacityCategory.Work);
        await Assert.That(Parse("Water plants ~p").Draft.Category).IsEqualTo(CapacityCategory.Personal);
        await Assert.That(Parse("Water plants").Draft.Category).IsNull();
    }

    [Test]
    public async Task Parse_UnknownOrSecondCategory_StaysInTheTitleWithAWarning()
    {
        var unknown = Parse("Plan trip ~holiday");
        await Assert.That(unknown.Draft.Title).IsEqualTo("Plan trip ~holiday");
        await Assert.That(unknown.Draft.Category).IsNull();
        await Assert.That(unknown.Warnings.Select(warning => warning.Code)).Contains("unknownCategory");

        var twice = Parse("Review ~work ~personal");
        await Assert.That(twice.Draft.Category).IsEqualTo(CapacityCategory.Work);
        await Assert.That(twice.Draft.Title).IsEqualTo("Review ~personal");
    }

    // ------------------------------------------------------------ SPEC.md examples

    [Test]
    public async Task Parse_SpecTomorrowAtThree_PlansTomorrowAtFifteen()
    {
        var result = Parse("Call plumber tomorrow 3pm");
        await Assert.That(result.Draft.Title).IsEqualTo("Call plumber");
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(Today.AddDays(1));
        await Assert.That(result.Draft.PlannedTime).IsEqualTo(new TimeOnly(15, 0));
        await Assert.That(result.Draft.DueDate).IsNull();
    }

    [Test]
    public async Task Parse_SpecByFridayTagPriority_SetsDueTagAndPriority()
    {
        var result = Parse("Submit report by Friday #work !high");
        await Assert.That(result.Draft.Title).IsEqualTo("Submit report");
        await Assert.That(result.Draft.DueDate).IsEqualTo(new DateOnly(2026, 11, 13));
        await Assert.That(result.Draft.PlannedDate).IsNull();
        await Assert.That(result.Draft.Priority).IsEqualTo(TaskPriority.High);
        await Assert.That(result.Draft.Tags.Single()).IsEqualTo(new CaptureTag("work", Existing: true));
    }

    [Test]
    public async Task Parse_SpecEveryMonday_RepeatsWeeklyFromNextMonday()
    {
        var result = Parse("Water plants every Monday");
        await Assert.That(result.Draft.Title).IsEqualTo("Water plants");
        var rule = result.Draft.Recurrence!;
        await Assert.That(rule.Frequency).IsEqualTo(RecurrenceFrequency.Week);
        await Assert.That(rule.Weekdays.ToArray()).IsEquivalentTo(new[] { DayOfWeek.Monday });
        await Assert.That(rule.Mode).IsEqualTo(RecurrenceMode.FixedSchedule);
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 16));
    }

    [Test]
    public async Task Parse_SpecBookClubSpaceAndList_KeepsSharedSpaceAndUsesList()
    {
        var result = Parse("Read chapter 4 by 15 Dec @book-club /Reading");
        await Assert.That(result.Draft.DueDate).IsEqualTo(new DateOnly(2026, 12, 15));
        await Assert.That(result.Draft.ContainerId).IsEqualTo("list-reading");
        await Assert.That(result.Draft.Title).IsEqualTo("Read chapter 4 @book-club");
        await Assert.That(result.Warnings.Select(warning => warning.Code)).Contains("unknownSpace");
        await Assert.That(result.Tokens.Single(token => token.Kind == CaptureTokenKind.Space).State).IsEqualTo(CaptureTokenState.Kept);
    }

    [Test]
    public async Task Parse_SpecNextTuesdayWithReminder_PlansNextWeekAndRemindsAnHourBefore()
    {
        var result = Parse("Dentist next Tue 10:30 remind me 1h before");
        await Assert.That(result.Draft.Title).IsEqualTo("Dentist");
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(new DateOnly(2026, 11, 17));
        await Assert.That(result.Draft.PlannedTime).IsEqualTo(new TimeOnly(10, 30));
        await Assert.That(result.Draft.Reminders.Single()).IsEqualTo(new CaptureReminder(CaptureReminderAnchor.Planned, -60, null));
    }

    [Test]
    public async Task Parse_SpecEstimate_SetsThirtyMinutes()
    {
        var result = Parse("Review PR for 30 min");
        await Assert.That(result.Draft.Title).IsEqualTo("Review PR");
        await Assert.That(result.Draft.EstimateMinutes).IsEqualTo(30);
    }

    [Test]
    public async Task Parse_SpecDelegationAndFollowUp_KeepsMemberAndSetsFollowUp()
    {
        var result = Parse("Send venue quote +Omar follow up Friday");
        await Assert.That(result.Draft.Title).IsEqualTo("Send venue quote +Omar");
        await Assert.That(result.Draft.FollowUpDate).IsEqualTo(new DateOnly(2026, 11, 13));
        await Assert.That(result.Warnings.Select(warning => warning.Code)).Contains("delegationLater");
    }

    // ------------------------------------------------------------ every language: planned, due, relative, repeat, reminder, estimate

    [Test]
    [Arguments("Call Sam tomorrow at 3pm", "Call Sam", "en")]
    [Arguments("Appeler Marie demain à 15h", "Appeler Marie", "fr")]
    [Arguments("Chiamare Luca domani alle 15", "Chiamare Luca", "it")]
    [Arguments("Llamar a Ana mañana a las 3 de la tarde", "Llamar a Ana", "es")]
    [Arguments("Telepon Budi besok jam 3 sore", "Telepon Budi", "id")]
    [Arguments("اتصل بأحمد غدًا الساعة ٣ م", "اتصل بأحمد", "ar")]
    public async Task Parse_TomorrowAtThreeInEachLanguage_PlansTomorrowAtFifteen(string text, string title, string language)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.Title).IsEqualTo(title);
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(Today.AddDays(1));
        await Assert.That(result.Draft.PlannedTime).IsEqualTo(new TimeOnly(15, 0));
        await Assert.That(result.Languages).Contains(language);
    }

    [Test]
    [Arguments("Submit report by Friday", "Submit report")]
    [Arguments("Rendre le rapport avant vendredi", "Rendre le rapport")]
    [Arguments("Consegnare la relazione entro venerdì", "Consegnare la relazione")]
    [Arguments("Entregar el informe para el viernes", "Entregar el informe")]
    [Arguments("Kirim laporan sebelum Jumat", "Kirim laporan")]
    [Arguments("تسليم التقرير قبل الجمعة", "تسليم التقرير")]
    public async Task Parse_DueFridayInEachLanguage_SetsDueNotPlanned(string text, string title)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.Title).IsEqualTo(title);
        await Assert.That(result.Draft.DueDate).IsEqualTo(new DateOnly(2026, 11, 13));
        await Assert.That(result.Draft.PlannedDate).IsNull();
    }

    [Test]
    [Arguments("Renew passport in 3 days")]
    [Arguments("Renouveler le passeport dans 3 jours")]
    [Arguments("Rinnovare il passaporto tra 3 giorni")]
    [Arguments("Renovar el pasaporte en 3 días")]
    [Arguments("Perpanjang paspor 3 hari lagi")]
    [Arguments("تجديد الجواز بعد ٣ أيام")]
    public async Task Parse_InThreeDaysInEachLanguage_PlansThreeDaysAhead(string text)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(Today.AddDays(3));
        await Assert.That(result.Draft.Title.Any(char.IsDigit)).IsFalse();
    }

    [Test]
    [Arguments("Water plants every Monday")]
    [Arguments("Arroser les plantes tous les lundis")]
    [Arguments("Annaffiare le piante ogni lunedì")]
    [Arguments("Regar las plantas cada lunes")]
    [Arguments("Siram tanaman setiap hari Senin")]
    [Arguments("سقي النباتات كل يوم الاثنين")]
    public async Task Parse_EveryMondayInEachLanguage_RepeatsOnMondays(string text)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.Recurrence!.Weekdays.ToArray()).IsEquivalentTo(new[] { DayOfWeek.Monday });
        await Assert.That(result.Draft.Recurrence.Frequency).IsEqualTo(RecurrenceFrequency.Week);
    }

    [Test]
    [Arguments("Clean filter every 3 months after done")]
    [Arguments("Nettoyer le filtre tous les 3 mois une fois terminé")]
    [Arguments("Pulire il filtro ogni 3 mesi dopo il completamento")]
    [Arguments("Limpiar el filtro cada 3 meses después de completar")]
    [Arguments("Bersihkan filter setiap 3 bulan setelah selesai")]
    [Arguments("تنظيف الفلتر كل ٣ أشهر بعد الانتهاء")]
    public async Task Parse_EveryThreeMonthsAfterDoneInEachLanguage_RepeatsFromCompletion(string text)
    {
        var rule = Parse(text).Draft.Recurrence!;
        await Assert.That(rule.Mode).IsEqualTo(RecurrenceMode.AfterCompletion);
        await Assert.That(rule.Frequency).IsEqualTo(RecurrenceFrequency.Month);
        await Assert.That(rule.Interval).IsEqualTo(3);
    }

    [Test]
    [Arguments("Review every other week", RecurrenceFrequency.Week, 2)]
    [Arguments("Revoir toutes les deux semaines", RecurrenceFrequency.Week, 2)]
    [Arguments("مراجعة كل أسبوعين", RecurrenceFrequency.Week, 2)]
    [Arguments("Backup daily", RecurrenceFrequency.Day, 1)]
    [Arguments("Laporan bulanan", RecurrenceFrequency.Month, 1)]
    [Arguments("Revisión cada 2 años", RecurrenceFrequency.Year, 2)]
    public async Task Parse_IntervalRepeats_ReadIntervalAndFrequency(string text, RecurrenceFrequency frequency, int interval)
    {
        var rule = Parse(text).Draft.Recurrence!;
        await Assert.That(rule.Frequency).IsEqualTo(frequency);
        await Assert.That(rule.Interval).IsEqualTo(interval);
    }

    [Test]
    public async Task Parse_MonthlyOnTheFirst_UsesMonthDay()
    {
        var result = Parse("Pay rent every month on the 1st");
        await Assert.That(result.Draft.Title).IsEqualTo("Pay rent");
        await Assert.That(result.Draft.Recurrence!.MonthDay).IsEqualTo(1);
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(new DateOnly(2026, 12, 1));
    }

    [Test]
    public async Task Parse_EveryWeekday_RepeatsMondayToFriday()
    {
        var rule = Parse("Standup every weekday 9:15").Draft.Recurrence!;
        await Assert.That(rule.Weekdays.Count).IsEqualTo(5);
        await Assert.That(rule.AnchorTime).IsEqualTo(new TimeOnly(9, 15));
    }

    [Test]
    [Arguments("Dentist tomorrow 10:30 remind me 1h before")]
    [Arguments("Dentiste demain 10h30 rappelle-moi 1 h avant")]
    [Arguments("Dentista domani alle 10:30 ricordami 1 ora prima")]
    [Arguments("Dentista mañana a las 10:30 recuérdame 1 hora antes")]
    [Arguments("Dokter gigi besok jam 10.30 ingatkan 1 jam sebelumnya")]
    [Arguments("طبيب الأسنان غدًا الساعة 10:30 ذكرني قبل ساعة")]
    public async Task Parse_ReminderHourBeforeInEachLanguage_AddsPlannedOffset(string text)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.PlannedTime).IsEqualTo(new TimeOnly(10, 30));
        await Assert.That(result.Draft.Reminders.Single()).IsEqualTo(new CaptureReminder(CaptureReminderAnchor.Planned, -60, null));
    }

    [Test]
    [Arguments("Review PR for 1h 30min", 90)]
    [Arguments("Lire le dossier pendant 1h30", 90)]
    [Arguments("Leggere per 45 min", 45)]
    [Arguments("Revisar PR durante 30 minutos", 30)]
    [Arguments("Rapat selama 2 jam 15 menit", 135)]
    [Arguments("مراجعة الملف لمدة ساعتين", 120)]
    public async Task Parse_EstimateInEachLanguage_SetsMinutes(string text, int minutes)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.EstimateMinutes).IsEqualTo(minutes);
        await Assert.That(result.Draft.PlannedTime).IsNull();
    }

    [Test]
    [Arguments("Contract waiting on Omar")]
    [Arguments("Contrat en attente de Omar")]
    [Arguments("Contratto in attesa di Omar")]
    [Arguments("Contrato esperando a Omar")]
    [Arguments("Kontrak menunggu Omar")]
    [Arguments("العقد بانتظار Omar")]
    public async Task Parse_WaitingInEachLanguage_RecordsPersonWithOriginalCase(string text)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.WaitingOn).IsEqualTo("Omar");
        await Assert.That(result.Draft.Title.Contains("Omar")).IsFalse();
    }

    [Test]
    [Arguments("!urgent Fix leak", TaskPriority.Urgent, "en")]
    [Arguments("Réparer la fuite !haute", TaskPriority.High, "fr")]
    [Arguments("Riparare !bassa", TaskPriority.Low, "it")]
    [Arguments("Arreglar !baja", TaskPriority.Low, "es")]
    [Arguments("Perbaiki !mendesak", TaskPriority.Urgent, "id")]
    [Arguments("إصلاح التسريب !عاجلة", TaskPriority.Urgent, "ar")]
    public async Task Parse_PriorityAliasInEachLanguage_SetsPriority(string text, TaskPriority priority, string language)
    {
        var result = Parse(text);
        await Assert.That(result.Draft.Priority).IsEqualTo(priority);
        await Assert.That(result.Languages).Contains(language);
    }

    // ------------------------------------------------------------ edge cases from the capture matrix

    [Test]
    public async Task Parse_MixedLanguages_CombinesFrenchDateWithEnglishTime()
    {
        var result = Parse("Appeler Sam demain 3pm #travail");
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(Today.AddDays(1));
        await Assert.That(result.Draft.PlannedTime).IsEqualTo(new TimeOnly(15, 0));
        await Assert.That(result.Draft.Tags.Single()).IsEqualTo(new CaptureTag("travail", Existing: false));
        await Assert.That(result.Languages).Contains("fr");
        await Assert.That(result.Languages).Contains("en");
    }

    [Test]
    public async Task Parse_ArabicIndicDigits_ReadAsWesternDigits()
    {
        var result = Parse("دفع الإيجار قبل ١٥/١٢ لمدة ٣٠ دقيقة");
        await Assert.That(result.Draft.DueDate).IsEqualTo(new DateOnly(2026, 12, 15));
        await Assert.That(result.Draft.EstimateMinutes).IsEqualTo(30);
        await Assert.That(result.Draft.Title).IsEqualTo("دفع الإيجار");
    }

    [Test]
    public async Task Parse_EmojiBeforeDate_ReturnsOriginalUtf16Span()
    {
        var text = "🎉 Party tomorrow";
        var token = Parse(text).Tokens.Single();
        await Assert.That(token.Start).IsEqualTo(9);
        await Assert.That(token.End).IsEqualTo(text.Length);
        await Assert.That(text[token.Start..token.End]).IsEqualTo("tomorrow");
    }

    [Test]
    public async Task Parse_CombiningMarks_SpanCoversWholeSourceWord()
    {
        var text = "Llamar mañana";
        var result = Parse(text);
        var token = result.Tokens.Single();
        await Assert.That(text[token.Start..token.End]).IsEqualTo("mañana");
        await Assert.That(result.Draft.Title).IsEqualTo("Llamar");
    }

    [Test]
    public async Task Parse_LiteralMay_StaysInTitle()
    {
        var result = Parse("Call May about the flat");
        await Assert.That(result.Tokens.Count).IsEqualTo(0);
        await Assert.That(result.Draft.Title).IsEqualTo("Call May about the flat");
    }

    [Test]
    public async Task Parse_MayWithDayNumber_IsTheNextFifthOfMay()
    {
        var result = Parse("Call May on 5 May");
        await Assert.That(result.Draft.Title).IsEqualTo("Call May");
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(new DateOnly(2027, 5, 5));
    }

    [Test]
    [Arguments("DMY", 2027, 4, 3)]
    [Arguments("MDY", 2027, 3, 4)]
    public async Task Parse_AmbiguousNumericDate_FollowsSettingAndWarns(string format, int year, int month, int day)
    {
        var result = Parse("Pay 03/04", Context(format: format));
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(new DateOnly(year, month, day));
        await Assert.That(result.Warnings.Select(warning => warning.Code)).Contains("ambiguousDate");
    }

    [Test]
    public async Task Parse_UnknownContainer_KeepsTextAndWarns()
    {
        var result = Parse("Buy milk /Groceries");
        await Assert.That(result.Draft.ContainerId).IsNull();
        await Assert.That(result.Draft.Title).IsEqualTo("Buy milk /Groceries");
        await Assert.That(result.Warnings.Single().Code).IsEqualTo("unknownContainer");
    }

    [Test]
    public async Task Parse_AmbiguousContainer_OffersBothCandidates()
    {
        var result = Parse("Post parcel /errands");
        await Assert.That(result.Draft.ContainerId).IsNull();
        await Assert.That(result.Draft.ContainerCandidates.Count).IsEqualTo(2);
        await Assert.That(result.Draft.Title).IsEqualTo("Post parcel");
    }

    [Test]
    public async Task Parse_ContainerNameWithSpace_MatchesKnownListWithoutQuotes()
    {
        var result = Parse("Pick a novel /Book Club soon");
        await Assert.That(result.Draft.ContainerId).IsEqualTo("list-book");
        await Assert.That(result.Draft.Title).IsEqualTo("Pick a novel soon");
    }

    [Test]
    public async Task Parse_MetadataOnly_AsksForTitle()
    {
        var result = Parse("#work !high tomorrow");
        await Assert.That(result.NeedsTitle).IsTrue();
        await Assert.That(result.Warnings.Select(warning => warning.Code)).Contains("needsTitle");
    }

    [Test]
    public async Task Parse_ExcludedToken_StaysPlainText()
    {
        var result = Parse("Meet Friday", Context(excluded: [new CaptureSpan(5, 11)]));
        await Assert.That(result.Tokens.Count).IsEqualTo(0);
        await Assert.That(result.Draft.Title).IsEqualTo("Meet Friday");
        await Assert.That(result.Draft.PlannedDate).IsNull();
    }

    [Test]
    public async Task Parse_TwoPlannedDates_KeepsSecondAsConflict()
    {
        var result = Parse("Meet tomorrow Friday");
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(Today.AddDays(1));
        await Assert.That(result.Draft.Title).IsEqualTo("Meet Friday");
        await Assert.That(result.Warnings.Single().Code).IsEqualTo("conflict");
    }

    [Test]
    [Arguments("Call 11pm", 2026, 11, 11)]
    [Arguments("Call 11:45pm", 2026, 11, 10)]
    [Arguments("Call tomorrow", 2026, 11, 11)]
    public async Task Parse_JustBeforeMidnight_ResolvesAgainstCapturedMoment(string text, int year, int month, int day)
    {
        var result = Parse(text, Context(now: new DateTimeOffset(2026, 11, 10, 23, 30, 0, TimeSpan.Zero)));
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(new DateOnly(year, month, day));
    }

    [Test]
    [Arguments("Gym Tuesday 8am", 2026, 11, 17)]
    [Arguments("Gym Tuesday 6pm", 2026, 11, 10)]
    [Arguments("Gym Tuesday", 2026, 11, 10)]
    public async Task Parse_BareWeekdayToday_UsesTodayOnlyWhileTimeIsAhead(string text, int year, int month, int day)
    {
        await Assert.That(Parse(text).Draft.PlannedDate).IsEqualTo(new DateOnly(year, month, day));
    }

    [Test]
    public async Task Parse_AbbreviatedWeekdayWithoutMarker_StaysText()
    {
        var result = Parse("Buy sun cream");
        await Assert.That(result.Tokens.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Parse_InTwoHours_PlansTodayWithTime()
    {
        var result = Parse("Check oven in 2 hours");
        await Assert.That(result.Draft.PlannedDate).IsEqualTo(Today);
        await Assert.That(result.Draft.PlannedTime).IsEqualTo(new TimeOnly(11, 0));
    }

    [Test]
    public async Task Parse_ReminderWithoutAnchor_KeepsTextAndWarns()
    {
        var result = Parse("Call mom remind me 1h before");
        await Assert.That(result.Draft.Reminders.Count).IsEqualTo(0);
        await Assert.That(result.Warnings.Single().Code).IsEqualTo("reminderNeedsAnchor");
        await Assert.That(result.Draft.Title).IsEqualTo("Call mom remind me 1h before");
    }

    [Test]
    public async Task Parse_AbsoluteReminderInDstGap_MovesForwardByTheGap()
    {
        var context = Context(now: new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), zone: "America/New_York");
        var result = Parse("File taxes remind me 2026-03-08 2:30am", context);
        await Assert.That(result.Draft.Reminders.Single().AtUtc).IsEqualTo(new DateTimeOffset(2026, 3, 8, 7, 30, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task LocalInstant_AmbiguousFallBackTime_TakesEarlierInstant()
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        await Assert.That(LocalInstant.ToUtc(new DateOnly(2026, 11, 1), new TimeOnly(1, 30), zone)).IsEqualTo(new DateTimeOffset(2026, 11, 1, 5, 30, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Parse_PersonalSpace_IsAppliedAndRemovedFromTitle()
    {
        var result = Parse("Stretch @personal #home/garden");
        await Assert.That(result.Draft.Title).IsEqualTo("Stretch");
        await Assert.That(result.Draft.Tags.Single()).IsEqualTo(new CaptureTag("home/garden", Existing: true));
    }

    [Test]
    public async Task Parse_SlashInsideDate_IsNotAContainer()
    {
        var result = Parse("Pay 15/12");
        await Assert.That(result.Tokens.Single().Kind).IsEqualTo(CaptureTokenKind.PlannedDate);
        await Assert.That(result.Warnings.Count).IsEqualTo(0);
    }
}

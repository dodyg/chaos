using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Chaos.Domain.Recurrence;
using Chaos.Domain.Tags;
using Chaos.Domain.Tasks;
using Chaos.Domain.Validation;

namespace Chaos.Domain.Capture;

/// <summary>
/// Pure quick-add parsing (PLAN §8): text plus a captured context in, a draft, recognized
/// spans in original UTF-16 offsets, language evidence and warnings out. No clock, storage
/// or tag creation. Matching runs over the folded comparison text with every language pack
/// at once, so mixed-language input works; longer phrases claim text first in this order:
/// recurrence, reminders, follow-up and waiting, due phrases, estimates, planned date/time,
/// then the #tag !priority /list @space +person ~category sigils.
/// </summary>
public static class QuickAddParser
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(500);
    private static readonly Regex TagSigil = new(@"(?<!\S)#(?<v>[\p{L}\p{M}\p{N}_\-]+(?:/[\p{L}\p{M}\p{N}_\-]+)*)", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex PrioritySigil = new(@"(?<!\S)!(?<v>[\p{L}\p{M}]+)(?![\p{L}\p{N}])", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex ContainerSigil = new(@"(?<!\S)/(?:""(?<q>[^""]+)""|(?<v>[\p{L}\p{M}\p{N}_\-]+))", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex SpaceSigil = new(@"(?<!\S)@(?<v>[\p{L}\p{M}\p{N}_\-]+)", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex DelegationSigil = new(@"(?<!\S)\+(?<v>\p{L}[\p{L}\p{M}\p{N}_\-]*)", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex CategorySigil = new(@"(?<!\S)~(?<v>\p{L}+)(?![\p{L}\p{N}])", RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex HourLetter = new(@"\d+h\d*", RegexOptions.CultureInvariant, MatchTimeout);
    private static readonly Regex AnyLetter = new(@"\p{L}", RegexOptions.CultureInvariant, MatchTimeout);

    // Declared after the helpers above: static initializers run in textual order.
    private static readonly IReadOnlyList<CompiledPack> Packs = [.. LanguagePacks.All.Select(pack => new CompiledPack(pack))];

    public static QuickAddResult Parse(string input, QuickAddContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(context.ZoneId);
        var localNow = TimeZoneInfo.ConvertTime(context.NowUtc, zone).DateTime;
        var clock = new Clock(DateOnly.FromDateTime(localNow), TimeOnly.FromDateTime(localNow), localNow, context);
        var folded = FoldedText.Fold(input);
        var accepted = new List<Candidate>();

        foreach (var phase in Enum.GetValues<Phase>())
        {
            var found = new List<(Candidate Candidate, int Order)>();
            for (var order = 0; order < Packs.Count; order++)
            {
                var pack = Packs[order];
                var regex = pack.For(phase);
                for (var match = regex.Match(folded.Value); match.Success; match = match.Index + 1 <= folded.Value.Length ? regex.Match(folded.Value, match.Index + 1) : Match.Empty)
                {
                    if (match.Length == 0) continue;
                    var (start, end) = folded.ToSource(match.Index, match.Length);
                    if (IsBlocked(start, end, accepted, context.ExcludedSpans)) continue;
                    var payload = Interpret(phase, match, pack, clock);
                    if (payload is null) continue;
                    var language = HasWords(match.Value) ? pack.Code : null;
                    if (phase == Phase.Waiting && payload is string)
                    {
                        var name = match.Groups["wn"];
                        var (nameStart, nameEnd) = folded.ToSource(name.Index, name.Length);
                        payload = input[nameStart..nameEnd].Trim('"').TrimEnd('.', ',');
                    }

                    found.Add((new Candidate(KindOf(phase), start, end, language, payload), order));
                }
            }

            foreach (var (candidate, _) in found.OrderBy(item => item.Candidate.Start).ThenByDescending(item => item.Candidate.End - item.Candidate.Start).ThenBy(item => item.Order))
            {
                if (!IsBlocked(candidate.Start, candidate.End, accepted, [])) accepted.Add(candidate);
            }
        }

        AddSigils(input, context, accepted);
        return Resolve(input, accepted, clock, zone);
    }

    // ---------------------------------------------------------------- resolution

    private static QuickAddResult Resolve(string input, List<Candidate> candidates, Clock clock, TimeZoneInfo zone)
    {
        var context = clock.Context;
        var ordered = candidates.OrderBy(candidate => candidate.Start).ToList();
        var states = ordered.ToDictionary(candidate => candidate, _ => CaptureTokenState.Applied);
        var summaries = new Dictionary<Candidate, string>();
        var warnings = new List<CaptureWarning>();

        void Keep(Candidate candidate, string code, string message)
        {
            states[candidate] = CaptureTokenState.Kept;
            warnings.Add(new CaptureWarning(code, message, candidate.Start, candidate.End));
        }

        string Text(Candidate candidate) => input[candidate.Start..candidate.End];
        IEnumerable<Candidate> Of(CaptureTokenKind kind) => ordered.Where(candidate => candidate.Kind == kind);

        // Planned and due: the first date wins; a lone time joins a date-only phrase; anything more is a visible conflict.
        (DateOnly? Date, TimeOnly? Time) Combine(CaptureTokenKind kind, string label)
        {
            DateOnly? date = null;
            TimeOnly? time = null;
            var bare = false;
            foreach (var candidate in Of(kind))
            {
                var moment = (Moment)candidate.Payload;
                if (moment.Date is not null && date is null && (moment.Time is null || time is null))
                {
                    date = moment.Date;
                    bare = moment.BareWeekday;
                    time ??= moment.Time;
                    if (moment.Note is not null) warnings.Add(new CaptureWarning("ambiguousDate", moment.Note, candidate.Start, candidate.End));
                }
                else if (moment.Date is null && moment.Time is not null && time is null)
                {
                    time = moment.Time;
                }
                else
                {
                    Keep(candidate, "conflict", $"There is already a {label} date or time, so “{Text(candidate)}” stays in the title. Click a highlight to switch which one counts.");
                }
            }

            if (bare && time is { } chosen && date == clock.Today && chosen <= clock.NowTime)
            {
                date = date.Value.AddDays(7); // a bare weekday means its next occurrence still ahead
            }

            return (date, time);
        }

        var (plannedDate, plannedTime) = Combine(CaptureTokenKind.PlannedDate, "planned");
        var (dueDate, dueTime) = Combine(CaptureTokenKind.DueDate, "due");

        RecurrenceRule? rule = null;
        DateTime? firstOccurrence = null;
        foreach (var candidate in Of(CaptureTokenKind.Recurrence))
        {
            if (rule is not null)
            {
                Keep(candidate, "conflict", $"Only one repeat rule fits a task, so “{Text(candidate)}” stays in the title.");
                continue;
            }

            var spec = (RepeatSpec)candidate.Payload;
            var anchorDate = plannedDate ?? dueDate ?? clock.Today;
            var anchorTime = plannedDate is not null ? plannedTime : dueDate is not null ? dueTime : plannedTime;
            var mode = spec.AfterDone ? RecurrenceMode.AfterCompletion : RecurrenceMode.FixedSchedule;
            if (spec.AfterDone && (spec.Weekdays.Count > 0 || spec.MonthDay is not null))
            {
                mode = RecurrenceMode.FixedSchedule;
                warnings.Add(new CaptureWarning("afterDoneCalendar", "“After done” works with plain intervals such as every 3 months. Repeats on chosen weekdays or month days follow the calendar instead.", candidate.Start, candidate.End));
            }

            var candidateRule = new RecurrenceRule(mode, spec.Frequency, spec.Interval, anchorDate, anchorTime, context.ZoneId,
                mode == RecurrenceMode.FixedSchedule ? spec.Weekdays : [], mode == RecurrenceMode.FixedSchedule && spec.Frequency == RecurrenceFrequency.Month ? spec.MonthDay : null,
                null, null, null, RecurrenceEndKind.Never, null, null);
            try
            {
                candidateRule.Validate();
                firstOccurrence = mode == RecurrenceMode.FixedSchedule
                    ? RecurrenceCalculator.Expand(candidateRule, anchorDate, anchorDate.AddYears(9), 1).Cast<DateTime?>().FirstOrDefault()
                    : anchorDate.ToDateTime(anchorTime ?? TimeOnly.MinValue);
                if (firstOccurrence is null) throw new DomainValidationException("This repeat rule never produces a date.");
                rule = candidateRule;
                summaries[candidate] = $"Repeats {DescribeRule(candidateRule)}; first {Describe(DateOnly.FromDateTime(firstOccurrence.Value), anchorTime)}";
            }
            catch (DomainValidationException error)
            {
                Keep(candidate, "invalidRepeat", $"“{Text(candidate)}” cannot repeat as written: {error.Message}");
            }
        }

        if (rule is not null && plannedDate is null && dueDate is null && firstOccurrence is { } first)
        {
            plannedDate = DateOnly.FromDateTime(first);
        }

        if (plannedDate is null && plannedTime is { } loneTime)
        {
            plannedDate = loneTime > clock.NowTime ? clock.Today : clock.Today.AddDays(1);
        }

        foreach (var candidate in Of(CaptureTokenKind.PlannedDate))
        {
            if (states[candidate] == CaptureTokenState.Applied) summaries[candidate] = $"Planned {Describe(plannedDate, plannedTime)}";
        }

        foreach (var candidate in Of(CaptureTokenKind.DueDate))
        {
            if (states[candidate] == CaptureTokenState.Applied) summaries[candidate] = $"Due {Describe(dueDate, dueTime)}";
        }

        var reminders = new List<CaptureReminder>();
        foreach (var candidate in Of(CaptureTokenKind.Reminder))
        {
            var spec = (ReminderSpec)candidate.Payload;
            if (spec.OffsetMinutes is { } offset)
            {
                var anchor = plannedDate is not null ? CaptureReminderAnchor.Planned : dueDate is not null ? CaptureReminderAnchor.Due : (CaptureReminderAnchor?)null;
                if (anchor is null)
                {
                    Keep(candidate, "reminderNeedsAnchor", $"“{Text(candidate)}” needs a planned or due date to count back from. Add a date, or remove the reminder.");
                    continue;
                }

                reminders.Add(new CaptureReminder(anchor.Value, -offset, null));
                summaries[candidate] = $"Reminder {DescribeDuration(offset)} before the {(anchor == CaptureReminderAnchor.Planned ? "planned" : "due")} time";
            }
            else
            {
                var moment = spec.At!;
                var time = moment.Time ?? context.DefaultReminderTime;
                var date = moment.Date ?? (time > clock.NowTime ? clock.Today : clock.Today.AddDays(1));
                var instant = LocalInstant.ToUtc(date, time, zone);
                reminders.Add(new CaptureReminder(CaptureReminderAnchor.Absolute, null, instant));
                summaries[candidate] = $"Reminder {Describe(date, time)}";
                if (instant <= context.NowUtc) warnings.Add(new CaptureWarning("reminderInPast", "That reminder time has already passed. Pick a later time.", candidate.Start, candidate.End));
            }
        }

        int? estimate = null;
        foreach (var candidate in Of(CaptureTokenKind.Estimate))
        {
            var minutes = (int)candidate.Payload;
            if (estimate is not null) Keep(candidate, "conflict", $"There is already an estimate, so “{Text(candidate)}” stays in the title.");
            else if (minutes is < 1 or > EstimateMinutes.MaxMinutes) Keep(candidate, "estimateRange", $"An estimate can be 1 to {EstimateMinutes.MaxMinutes} minutes; “{Text(candidate)}” stays in the title.");
            else { estimate = minutes; summaries[candidate] = $"Estimate {DescribeDuration(minutes)}"; }
        }

        TaskPriority? priority = null;
        foreach (var candidate in Of(CaptureTokenKind.Priority))
        {
            if (priority is not null) { Keep(candidate, "conflict", $"There is already a priority, so “{Text(candidate)}” stays in the title."); continue; }
            priority = (TaskPriority)candidate.Payload;
            summaries[candidate] = $"Priority {priority}";
        }

        var knownTags = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in context.TagPaths)
        {
            if (TryTagKey(path) is { } key) knownTags.Add(key);
        }

        var tags = new List<CaptureTag>();
        var tagKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in Of(CaptureTokenKind.Tag))
        {
            var path = (string)candidate.Payload;
            var key = TryTagKey(path);
            if (key is null) { Keep(candidate, "invalidTag", $"“{Text(candidate)}” is not a valid tag name, so it stays in the title."); continue; }
            var existing = knownTags.Contains(key);
            if (tagKeys.Add(key)) tags.Add(new CaptureTag(path, existing));
            summaries[candidate] = existing ? $"Tag #{path}" : $"New tag #{path}";
        }

        string? containerId = null;
        IReadOnlyList<KnownContainer> containerCandidates = [];
        var containerChosen = false;
        foreach (var candidate in Of(CaptureTokenKind.Container))
        {
            if (containerChosen) { Keep(candidate, "conflict", $"There is already a list or project, so “{Text(candidate)}” stays in the title."); continue; }
            var name = (string)candidate.Payload;
            var key = FoldedText.FoldWord(name.Trim());
            var matches = context.Containers.Where(container => FoldedText.FoldWord(container.Name.Trim()) == key).ToList();
            if (matches.Count == 0)
            {
                Keep(candidate, "unknownContainer", $"There is no list or project named “{name}”, so “{Text(candidate)}” stays in the title. Create it first or pick one in the preview.");
                continue;
            }

            containerChosen = true;
            if (matches.Count == 1)
            {
                containerId = matches[0].Id;
                summaries[candidate] = $"In {matches[0].Name}";
            }
            else
            {
                containerCandidates = matches;
                warnings.Add(new CaptureWarning("ambiguousContainer", $"More than one list or project is named “{name}”. Pick the right one.", candidate.Start, candidate.End));
                summaries[candidate] = $"In {name} (choose which)";
            }
        }

        foreach (var candidate in Of(CaptureTokenKind.Space))
        {
            if ((bool)candidate.Payload) summaries[candidate] = "Personal space";
            else Keep(candidate, "unknownSpace", $"Only your Personal space exists for now, so “{Text(candidate)}” stays in the title. Shared spaces arrive later.");
        }

        foreach (var candidate in Of(CaptureTokenKind.Delegation))
        {
            Keep(candidate, "delegationLater", $"Assigning “{Text(candidate)}” to a member arrives with shared spaces, so it stays in the title. For someone outside the app, write “waiting on” and their name.");
        }

        CapacityCategory? category = null;
        foreach (var candidate in Of(CaptureTokenKind.Category))
        {
            if (candidate.Payload is not CapacityCategory chosen) { Keep(candidate, "unknownCategory", $"Use ~work or ~personal for a category, so “{Text(candidate)}” stays in the title."); continue; }
            if (category is not null) { Keep(candidate, "conflict", $"There is already a category, so “{Text(candidate)}” stays in the title."); continue; }
            category = chosen;
            summaries[candidate] = chosen == CapacityCategory.Work ? "Work" : "Personal";
        }

        string? waitingOn = null;
        foreach (var candidate in Of(CaptureTokenKind.Waiting))
        {
            if (waitingOn is not null) { Keep(candidate, "conflict", $"There is already a waiting person, so “{Text(candidate)}” stays in the title."); continue; }
            waitingOn = (string)candidate.Payload;
            summaries[candidate] = $"Waiting on {waitingOn}";
        }

        DateOnly? followUpDate = null;
        TimeOnly? followUpTime = null;
        foreach (var candidate in Of(CaptureTokenKind.FollowUp))
        {
            if (followUpDate is not null) { Keep(candidate, "conflict", $"There is already a follow-up, so “{Text(candidate)}” stays in the title."); continue; }
            var moment = (Moment)candidate.Payload;
            followUpTime = moment.Time;
            followUpDate = moment.Date ?? (moment.Time is { } at && at > clock.NowTime ? clock.Today : clock.Today.AddDays(1));
            summaries[candidate] = $"Follow up {Describe(followUpDate, followUpTime)}";
        }

        var title = BuildTitle(input, ordered.Where(candidate => states[candidate] == CaptureTokenState.Applied));
        var needsTitle = title.Length == 0;
        if (needsTitle) warnings.Add(new CaptureWarning("needsTitle", "Everything typed became a detail. Add a few words for the title before saving."));

        var tokens = ordered.Select(candidate => new CaptureToken(candidate.Kind, candidate.Start, candidate.End, Text(candidate), candidate.Language, states[candidate],
            summaries.TryGetValue(candidate, out var summary) ? summary : Text(candidate))).ToList();
        var draft = new QuickAddDraft
        {
            Title = title,
            PlannedDate = plannedDate,
            PlannedTime = plannedTime,
            DueDate = dueDate,
            DueTime = dueTime,
            ZoneId = context.ZoneId,
            Priority = priority,
            EstimateMinutes = estimate,
            Recurrence = rule,
            FirstOccurrence = firstOccurrence,
            Reminders = reminders,
            Tags = tags,
            ContainerId = containerId,
            ContainerCandidates = containerCandidates,
            WaitingOn = waitingOn,
            FollowUpDate = followUpDate,
            FollowUpTime = followUpTime,
            Category = category,
        };
        var languages = tokens.Where(token => token.Language is not null).Select(token => token.Language!).Distinct().ToList();
        return new QuickAddResult(input, draft, tokens, warnings, languages, needsTitle);
    }

    private static string BuildTitle(string input, IEnumerable<Candidate> removed)
    {
        var builder = new StringBuilder(input.Length);
        var cursor = 0;
        foreach (var candidate in removed.OrderBy(candidate => candidate.Start))
        {
            if (candidate.Start > cursor) builder.Append(input, cursor, candidate.Start - cursor);
            builder.Append(' ');
            cursor = Math.Max(cursor, candidate.End);
        }

        if (cursor < input.Length) builder.Append(input, cursor, input.Length - cursor);
        return Whitespace.Replace(builder.ToString(), " ").Trim().Trim(',', ';', ':', '-', '–', '—', '،').Trim();
    }

    private static string? TryTagKey(string path)
    {
        try { return string.Join('/', TagName.Path(path).Select(segment => segment.NormalizedName)); }
        catch (DomainValidationException) { return null; }
    }

    // ---------------------------------------------------------------- sigils

    private static void AddSigils(string input, QuickAddContext context, List<Candidate> accepted)
    {
        void Add(Candidate candidate)
        {
            if (!IsBlocked(candidate.Start, candidate.End, accepted, context.ExcludedSpans)) accepted.Add(candidate);
        }

        foreach (Match match in TagSigil.Matches(input))
        {
            Add(new Candidate(CaptureTokenKind.Tag, match.Index, match.Index + match.Length, null, match.Groups["v"].Value));
        }

        foreach (Match match in PrioritySigil.Matches(input))
        {
            var word = FoldedText.FoldWord(match.Groups["v"].Value);
            foreach (var pack in Packs)
            {
                if (pack.Priorities.TryGetValue(word, out var priority))
                {
                    Add(new Candidate(CaptureTokenKind.Priority, match.Index, match.Index + match.Length, pack.Code, priority));
                    break;
                }
            }
        }

        foreach (Match match in ContainerSigil.Matches(input))
        {
            if (match.Groups["q"].Success)
            {
                Add(new Candidate(CaptureTokenKind.Container, match.Index, match.Index + match.Length, null, match.Groups["q"].Value));
                continue;
            }

            // An unquoted name may continue with spaces when a known list is named that way (/Book Club).
            var nameStart = match.Index + 1;
            var rest = FoldedText.Fold(input[nameStart..]);
            var end = match.Index + match.Length;
            var name = match.Groups["v"].Value;
            foreach (var container in context.Containers.OrderByDescending(container => container.Name.Length))
            {
                var key = FoldedText.FoldWord(container.Name.Trim());
                if (key.Length == 0 || !rest.Value.StartsWith(key, StringComparison.Ordinal)) continue;
                if (rest.Value.Length > key.Length && (char.IsLetterOrDigit(rest.Value[key.Length]) || rest.Value[key.Length] == '_')) continue;
                var (_, mappedEnd) = rest.ToSource(0, key.Length);
                if (nameStart + mappedEnd >= end)
                {
                    end = nameStart + mappedEnd;
                    name = input[nameStart..end];
                }

                break;
            }

            Add(new Candidate(CaptureTokenKind.Container, match.Index, end, null, name));
        }

        foreach (Match match in SpaceSigil.Matches(input))
        {
            var word = FoldedText.FoldWord(match.Groups["v"].Value);
            var personal = Packs.FirstOrDefault(pack => pack.PersonalSpace.Contains(word));
            Add(new Candidate(CaptureTokenKind.Space, match.Index, match.Index + match.Length, personal?.Code, personal is not null));
        }

        foreach (Match match in DelegationSigil.Matches(input))
        {
            Add(new Candidate(CaptureTokenKind.Delegation, match.Index, match.Index + match.Length, null, match.Groups["v"].Value));
        }

        foreach (Match match in CategorySigil.Matches(input))
        {
            CapacityCategory? category = FoldedText.FoldWord(match.Groups["v"].Value) switch
            {
                "work" or "w" => CapacityCategory.Work,
                "personal" or "p" or "home" => CapacityCategory.Personal,
                _ => null,
            };
            Add(new Candidate(CaptureTokenKind.Category, match.Index, match.Index + match.Length, null, category ?? (object)match.Groups["v"].Value));
        }
    }

    // ---------------------------------------------------------------- interpretation

    private static object? Interpret(Phase phase, Match match, CompiledPack pack, Clock clock) => phase switch
    {
        Phase.Recurrence => ReadRepeat(match, pack),
        Phase.ReminderRelative => (ReadDuration(match, pack, "rn", "ru") ?? ReadDuration(match, pack, "rn2", "ru2")) is { } minutes ? new ReminderSpec(minutes, null) : null,
        Phase.ReminderAbsolute => ReadMoment(match, pack, clock, marked: true) is { } at ? new ReminderSpec(null, at) : null,
        Phase.FollowUp => ReadMoment(match, pack, clock, marked: true),
        Phase.Waiting => string.Empty, // the name is copied from the source by the caller
        Phase.Due => ReadMoment(match, pack, clock, marked: true),
        Phase.Estimate => ReadEstimate(match, pack),
        Phase.Planned => ReadMoment(match, pack, clock, marked: false),
        _ => null,
    };

    /// <summary>Reads "d" date + "t" time, or "u" time + "e" date (the two orders of a phrase).</summary>
    private static Moment? ReadMoment(Match match, CompiledPack pack, Clock clock, bool marked)
    {
        DateSpec? date = null;
        TimeOnly? time = null;
        foreach (var prefix in new[] { "d", "e" })
        {
            if (HasDate(match, prefix))
            {
                date = ReadDate(match, prefix, pack, clock, marked);
                if (date is null) return null;
            }
        }

        foreach (var prefix in new[] { "t", "u" })
        {
            if (HasTime(match, prefix))
            {
                time = ReadTime(match, prefix, pack);
                if (time is null) return null;
            }
        }

        if (date is null && time is null) return null;
        if (date?.Time is { } relativeTime)
        {
            if (time is not null) return null; // "in 2 hours at 5pm" is contradictory
            time = relativeTime;
        }

        return new Moment(date?.Date, time, date?.BareWeekday ?? false, date?.Note);
    }

    private static bool HasDate(Match match, string p) =>
        new[] { "dat", "tod", "tom", "inu", "isu", "wd", "m", "m2", "iy", "na", "na2" }.Any(name => match.Groups[p + name].Success);

    private static bool HasTime(Match match, string p) =>
        new[] { "h", "h2", "h3", "h4", "noon", "mid" }.Any(name => match.Groups[p + name].Success);

    private static DateSpec? ReadDate(Match match, string p, CompiledPack pack, Clock clock, bool marked)
    {
        var today = clock.Today;
        Group G(string name) => match.Groups[p + name];

        if (G("dat").Success) return new DateSpec(today.AddDays(2));
        if (G("tod").Success) return new DateSpec(today);
        if (G("tom").Success) return new DateSpec(today.AddDays(1));
        if (G("inu").Success || G("isu").Success)
        {
            var (number, unit) = G("inu").Success ? (G("inn"), G("inu")) : (G("isn"), G("isu"));
            return InDuration(number, unit, pack, clock);
        }

        if (G("wd").Success)
        {
            var key = Key(G("wd").Value);
            var day = pack.Weekdays[key];
            var next = G("np").Success || G("ns").Success;
            if (pack.Abbreviations.Contains(key) && !marked && !next && !OnMarked(match, p, pack)) return null;
            if (next)
            {
                var first = clock.Context.FirstWeekday;
                var weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)first + 7) % 7));
                return new DateSpec(weekStart.AddDays(7 + ((int)day - (int)first + 7) % 7));
            }

            var ahead = ((int)day - (int)today.DayOfWeek + 7) % 7;
            return new DateSpec(today.AddDays(ahead), BareWeekday: ahead == 0);
        }

        if (G("m").Success || G("m2").Success)
        {
            var (dayGroup, monthGroup, yearGroup) = G("m").Success ? (G("d"), G("m"), G("y")) : (G("d2"), G("m2"), G("y2"));
            var dayNumber = int.Parse(dayGroup.Value, CultureInfo.InvariantCulture);
            var month = pack.Months[Key(monthGroup.Value)];
            return CalendarDate(dayNumber, month, yearGroup.Success ? int.Parse(yearGroup.Value, CultureInfo.InvariantCulture) : null, today, note: null);
        }

        if (G("iy").Success)
        {
            return CalendarDate(Int(G("id")), Int(G("im")), Int(G("iy")), today, note: null);
        }

        if (G("na").Success || G("na2").Success)
        {
            var (a, b, c) = G("na").Success ? (Int(G("na")), Int(G("nb")), G("nc").Success ? Int(G("nc")) : (int?)null) : (Int(G("na2")), Int(G("nb2")), Int(G("nc2")));
            var monthFirst = clock.Context.DateFormat is "MDY" or "YMD";
            var (dayNumber, month) = monthFirst ? (b, a) : (a, b);
            int? year = c is { } given ? (given < 100 ? 2000 + given : given) : null;
            var result = CalendarDate(dayNumber, month, year, today, note: null);
            if (result is not null && a <= 12 && b <= 12 && a != b)
            {
                var order = monthFirst ? "month/day" : "day/month";
                result = result with { Note = $"Read “{match.Value.Trim()}” as {result.Date.ToString("d MMMM yyyy", CultureInfo.InvariantCulture)} using your {order} setting." };
            }

            return result;
        }

        return null;
    }

    /// <summary>Abbreviated weekdays count only after a marker: "on Tue", "le mar".</summary>
    private static bool OnMarked(Match match, string p, CompiledPack pack)
    {
        var text = match.Value[..(match.Groups[p + "wd"].Index - match.Index)];
        return pack.OnMarkerPattern.IsMatch(text);
    }

    private static DateSpec? InDuration(Group number, Group unitGroup, CompiledPack pack, Clock clock)
    {
        var unit = pack.Units[Key(unitGroup.Value)];
        decimal count;
        if (number.Success)
        {
            if (ParseNumber(number.Value, pack) is not { } parsed) return null;
            count = parsed;
        }
        else if (unit.Count > 1 || pack.Pack.BareUnits)
        {
            count = 1;
        }
        else
        {
            return null;
        }

        count *= unit.Count;
        if (count <= 0) return null;
        if (unit.Unit is DurationUnit.Minute or DurationUnit.Hour)
        {
            var minutes = unit.Unit == DurationUnit.Hour ? count * 60 : count;
            if (minutes > 60 * 24 * 7 || minutes != decimal.Truncate(minutes)) return null;
            var at = clock.LocalNow.AddMinutes((double)minutes);
            at = new DateTime(at.Year, at.Month, at.Day, at.Hour, at.Minute, 0);
            return new DateSpec(DateOnly.FromDateTime(at), Time: TimeOnly.FromDateTime(at));
        }

        if (count != decimal.Truncate(count) || count > 3650) return null;
        var whole = (int)count;
        return unit.Unit switch
        {
            DurationUnit.Day => new DateSpec(clock.Today.AddDays(whole)),
            DurationUnit.Week => new DateSpec(clock.Today.AddDays(7 * whole)),
            DurationUnit.Month => new DateSpec(clock.Today.AddMonths(whole)),
            _ => new DateSpec(clock.Today.AddYears(whole)),
        };
    }

    /// <summary>A calendar date; a yearless date is its next occurrence on or after today (Feb 29 waits for a leap year).</summary>
    private static DateSpec? CalendarDate(int day, int month, int? year, DateOnly today, string? note)
    {
        if (month is < 1 or > 12 || day is < 1 or > 31) return null;
        if (year is { } fixedYear)
        {
            return fixedYear is >= 1900 and <= 9998 && day <= DateTime.DaysInMonth(fixedYear, month) ? new DateSpec(new DateOnly(fixedYear, month, day), Note: note) : null;
        }

        for (var candidate = today.Year; candidate <= today.Year + 8; candidate++)
        {
            if (day > DateTime.DaysInMonth(candidate, month)) continue;
            var date = new DateOnly(candidate, month, day);
            if (date >= today) return new DateSpec(date, Note: note);
        }

        return null;
    }

    private static TimeOnly? ReadTime(Match match, string p, CompiledPack pack)
    {
        Group G(string name) => match.Groups[p + name];
        if (G("noon").Success) return new TimeOnly(12, 0);
        if (G("mid").Success) return new TimeOnly(0, 0);

        if (G("h").Success)
        {
            var hour = Int(G("h"));
            var minute = G("mi").Success ? Int(G("mi")) : 0;
            var marker = Key(G("ap").Value);
            if (minute > 59) return null;
            if (pack.Midday.Contains(marker))
            {
                if (hour > 23) return null;
                return new TimeOnly(hour is >= 1 and <= 6 ? hour + 12 : hour, minute);
            }

            if (hour is < 1 or > 12) return null;
            var isPm = pack.Pm.Contains(marker);
            return new TimeOnly(isPm ? (hour == 12 ? 12 : hour + 12) : (hour == 12 ? 0 : hour), minute);
        }

        var (hourGroup, minuteGroup) = G("h2").Success ? (G("h2"), G("mi2")) : G("h3").Success ? (G("h3"), G("mi3")) : (G("h4"), G("mi4"));
        var h = Int(hourGroup);
        var m = minuteGroup.Success ? Int(minuteGroup) : 0;
        return h is >= 0 and <= 23 && m is >= 0 and <= 59 ? new TimeOnly(h, m) : null;
    }

    private static RepeatSpec? ReadRepeat(Match match, CompiledPack pack)
    {
        var afterDone = match.Groups["rafter"].Success;
        int? monthDay = match.Groups["rmd"].Success ? Int(match.Groups["rmd"]) : match.Groups["rmd2"].Success ? Int(match.Groups["rmd2"]) : null;
        if (monthDay is < 1 or > 31) return null;

        if (match.Groups["rw"].Success)
        {
            return new RepeatSpec(RecurrenceFrequency.Week, 1, pack.ReadWeekdays(match.Groups["rw"].Value), null, afterDone);
        }

        if (match.Groups["rwd"].Success)
        {
            return new RepeatSpec(RecurrenceFrequency.Week, 1, [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday], null, afterDone);
        }

        if (match.Groups["radv"].Success)
        {
            var (frequency, interval) = pack.Adverbs[Key(match.Groups["radv"].Value)];
            if (monthDay is not null && frequency != RecurrenceFrequency.Month) return null;
            return new RepeatSpec(frequency, interval, [], monthDay, afterDone);
        }

        if (match.Groups["ru"].Success)
        {
            var unit = pack.Units[Key(match.Groups["ru"].Value)];
            if (unit.Unit is DurationUnit.Minute or DurationUnit.Hour) return null;
            var count = 1m;
            if (match.Groups["ro"].Success) count = 2;
            else if (match.Groups["rn"].Success)
            {
                if (ParseNumber(match.Groups["rn"].Value, pack) is not { } parsed || parsed != decimal.Truncate(parsed)) return null;
                count = parsed;
            }

            var interval = (int)Math.Min(count * unit.Count, 1000);
            var frequency = unit.Unit switch
            {
                DurationUnit.Day => RecurrenceFrequency.Day,
                DurationUnit.Week => RecurrenceFrequency.Week,
                DurationUnit.Month => RecurrenceFrequency.Month,
                _ => RecurrenceFrequency.Year,
            };
            IReadOnlyList<DayOfWeek> weekdays = match.Groups["rw2"].Success ? pack.ReadWeekdays(match.Groups["rw2"].Value) : [];
            if ((weekdays.Count > 0 && frequency != RecurrenceFrequency.Week) || (monthDay is not null && frequency != RecurrenceFrequency.Month)) return null;
            return new RepeatSpec(frequency, interval, weekdays, monthDay, afterDone);
        }

        return null;
    }

    /// <summary>Minutes in "1h", "30 min", "1.5 hours", "ساعتين"; units of a day or longer are allowed for reminders.</summary>
    private static int? ReadDuration(Match match, CompiledPack pack, string numberGroup, string unitGroup)
    {
        if (!match.Groups[unitGroup].Success) return null;
        var unit = pack.Units[Key(match.Groups[unitGroup].Value)];
        decimal count;
        if (match.Groups[numberGroup].Success)
        {
            if (ParseNumber(match.Groups[numberGroup].Value, pack) is not { } parsed) return null;
            count = parsed;
        }
        else if (unit.Count > 1 || pack.Pack.BareUnits) count = 1;
        else return null;

        var minutes = count * unit.Count * unit.Unit switch
        {
            DurationUnit.Minute => 1,
            DurationUnit.Hour => 60,
            DurationUnit.Day => 1440,
            DurationUnit.Week => 10080,
            _ => 0,
        };
        return minutes is > 0 and <= 525_600 && minutes == decimal.Truncate(minutes) ? (int)minutes : null;
    }

    private static int? ReadEstimate(Match match, CompiledPack pack)
    {
        if (match.Groups["eh"].Success)
        {
            var extra = Int(match.Groups["em"]);
            return extra > 59 ? null : Int(match.Groups["eh"]) * 60 + extra;
        }

        foreach (var name in new[] { "eu1", "eu2" })
        {
            if (match.Groups[name].Success && pack.Units[Key(match.Groups[name].Value)].Unit is not (DurationUnit.Minute or DurationUnit.Hour)) return null;
        }

        var first = ReadDuration(match, pack, "en1", "eu1");
        if (first is null) return null;
        if (!match.Groups["eu2"].Success) return first;
        var second = ReadDuration(match, pack, "en2", "eu2");
        return second is null ? null : first + second;
    }

    private static decimal? ParseNumber(string text, CompiledPack pack)
    {
        var key = Key(text);
        if (pack.Numbers.TryGetValue(key, out var word)) return word;
        return decimal.TryParse(key.Replace(',', '.'), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    // ---------------------------------------------------------------- helpers

    private static bool IsBlocked(int start, int end, List<Candidate> accepted, IReadOnlyList<CaptureSpan> excluded) =>
        accepted.Any(candidate => start < candidate.End && candidate.Start < end) || excluded.Any(span => span.Overlaps(start, end));

    private static bool HasWords(string foldedMatch) => AnyLetter.IsMatch(HourLetter.Replace(foldedMatch, string.Empty));

    private static int Int(Group group) => int.Parse(group.Value, NumberStyles.None, CultureInfo.InvariantCulture);

    private static string Key(string foldedWord) => Whitespace.Replace(foldedWord.Trim(), " ");

    private static CaptureTokenKind KindOf(Phase phase) => phase switch
    {
        Phase.Recurrence => CaptureTokenKind.Recurrence,
        Phase.ReminderRelative or Phase.ReminderAbsolute => CaptureTokenKind.Reminder,
        Phase.FollowUp => CaptureTokenKind.FollowUp,
        Phase.Waiting => CaptureTokenKind.Waiting,
        Phase.Due => CaptureTokenKind.DueDate,
        Phase.Estimate => CaptureTokenKind.Estimate,
        _ => CaptureTokenKind.PlannedDate,
    };

    private static string Describe(DateOnly? date, TimeOnly? time) =>
        (date?.ToString("ddd d MMM yyyy", CultureInfo.InvariantCulture) ?? "no date") + (time is { } at ? " at " + at.ToString("HH:mm", CultureInfo.InvariantCulture) : string.Empty);

    private static string DescribeDuration(int minutes) =>
        minutes % 1440 == 0 ? $"{minutes / 1440} d" : minutes % 60 == 0 ? $"{minutes / 60} h" : minutes > 60 ? $"{minutes / 60} h {minutes % 60} min" : $"{minutes} min";

    private static string DescribeRule(RecurrenceRule rule)
    {
        var unit = rule.Frequency switch
        {
            RecurrenceFrequency.Day => "day",
            RecurrenceFrequency.Week => "week",
            RecurrenceFrequency.Month => "month",
            _ => "year",
        };
        var every = rule.Interval == 1 ? $"every {unit}" : $"every {rule.Interval} {unit}s";
        if (rule.Weekdays.Count > 0) every += " on " + string.Join(", ", rule.Weekdays.Select(day => day.ToString()[..3]));
        if (rule.MonthDay is { } monthDay) every += $" on day {monthDay}";
        return rule.Mode == RecurrenceMode.AfterCompletion ? every + " after done" : every;
    }

    private enum Phase
    {
        Recurrence,
        ReminderRelative,
        ReminderAbsolute,
        FollowUp,
        Waiting,
        Due,
        Estimate,
        Planned,
    }

    private sealed record Candidate(CaptureTokenKind Kind, int Start, int End, string? Language, object Payload);

    private sealed record DateSpec(DateOnly Date, bool BareWeekday = false, TimeOnly? Time = null, string? Note = null);

    private sealed record Moment(DateOnly? Date, TimeOnly? Time, bool BareWeekday, string? Note);

    private sealed record RepeatSpec(RecurrenceFrequency Frequency, int Interval, IReadOnlyList<DayOfWeek> Weekdays, int? MonthDay, bool AfterDone);

    private sealed record ReminderSpec(int? OffsetMinutes, Moment? At);

    private sealed record Clock(DateOnly Today, TimeOnly NowTime, DateTime LocalNow, QuickAddContext Context);

    /// <summary>One language pack's folded vocabulary and its compiled phrase patterns.</summary>
    private sealed class CompiledPack
    {
        private const string L = @"(?<![\p{L}\p{N}])";
        private const string R = @"(?![\p{L}\p{N}])";
        private readonly Dictionary<Phase, Regex> _patterns = [];
        private readonly Regex _weekdayItem;

        public CompiledPack(LanguagePack pack)
        {
            Pack = pack;
            Code = pack.Code;
            Numbers = Fold(pack.NumberWords);
            Units = Fold(pack.Units);
            Weekdays = Fold(pack.Weekdays);
            var abbreviations = Fold(pack.WeekdayAbbreviations);
            foreach (var (key, day) in abbreviations) Weekdays.TryAdd(key, day);
            Abbreviations = [.. abbreviations.Keys.Where(key => !Fold(pack.Weekdays).ContainsKey(key))];
            Months = Fold(pack.Months);
            Adverbs = Fold(pack.RepeatAdverbs);
            Priorities = Fold(pack.Priorities);
            Pm = [.. pack.Pm.Select(Folded)];
            Midday = [.. pack.Midday.Select(Folded)];
            PersonalSpace = [.. pack.PersonalSpace.Select(Folded)];

            var weekday = $"{Alt(Weekdays.Keys)}{R}";
            var separator = $@"\s*(?:,|،|{Alt(pack.And)})\s*(?:{Alt(pack.OnMarkers)}\s+)?";
            string WeekdayList(string name) => $"(?<{name}>{weekday}(?:{separator}{weekday})*)";
            _weekdayItem = Compile(weekday);
            OnMarkerPattern = Compile($@"^(?:{Alt(pack.NextPrefix)}\s+)?{Alt(pack.OnMarkers)}\s+$");

            var on = Alt(pack.OnMarkers);
            var daySuffix = Alt(pack.DaySuffixes);
            var monthDay = $@"\s+{Alt(pack.MonthDayMarkers)}\s+(?<rmdNAME>\d{{1,2}})(?:{daySuffix})?{R}";
            _patterns[Phase.Recurrence] = Compile(L + "(?:" + string.Join("|",
                $@"{Alt(pack.Every)}\s+(?:{on}\s+)?{WeekdayList("rw")}",
                $@"{Alt(pack.Every)}\s+(?<rwd>{Alt(pack.WeekdaysWord)}){R}",
                $@"{Alt(pack.Every)}\s+(?:(?<ro>{Alt(pack.Other)})\s+|{Number("rn")}\s*)?{Unit("ru")}(?:{monthDay.Replace("NAME", "")})?(?:\s+(?:{on}\s+)?{WeekdayList("rw2")})?",
                $@"(?<radv>{Alt(Adverbs.Keys)}){R}(?:{monthDay.Replace("NAME", "2")})?") +
                $@")(?:\s+(?<rafter>{Alt(pack.AfterDone)}))?{R}");
            _patterns[Phase.ReminderRelative] = Compile($@"{L}{Alt(pack.RemindMe)}\s+(?:(?:{Number("rn")}\s*)?{Unit("ru")}\s+{Alt(pack.Before)}|{Alt(pack.Before)}\s+(?:{Number("rn2")}\s*)?{Unit("ru2")}){R}");
            _patterns[Phase.ReminderAbsolute] = Compile($@"{L}{Alt(pack.RemindMe)}\s+(?:{on}\s+)?(?:{Date("d")}(?:\s*,?\s*{Time("t")})?|{Time("u")}(?:\s+{Date("e")})?){R}");
            _patterns[Phase.FollowUp] = Compile($@"{L}{Alt(pack.FollowUpMarkers)}\s+(?:{on}\s+)?(?:{Date("d")}(?:\s*,?\s*{Time("t")})?|{Time("u")}(?:\s+{Date("e")})?){R}");
            _patterns[Phase.Waiting] = Compile($@"{L}{Alt(pack.WaitingMarkers)}\s+(?<wn>""[^""]+""|\p{{L}}[\p{{L}}\p{{N}}'\-]*)");
            _patterns[Phase.Due] = Compile($@"{L}{Alt(pack.DueMarkers)}\s+(?:{on}\s+)?(?:{Date("d")}(?:\s*,?\s*{Time("t")})?|{Time("u")}(?:\s+{Date("e")})?){R}");
            _patterns[Phase.Estimate] = Compile($@"{L}{Alt(pack.EstimateMarkers)}\s+(?:(?<eh>\d{{1,2}})h(?<em>\d{{2}})|(?:{Number("en1")}\s*)?{Unit("eu1")}(?:\s*(?:{Alt(pack.And)}\s*)?{Number("en2")}\s*{Unit("eu2")})?){R}");
            _patterns[Phase.Planned] = Compile($@"{L}(?:{Date("d")}(?:\s*,?\s*{Time("t")})?|{Time("u")}(?:\s+{Date("e")})?){R}");

            string Number(string name) => $@"(?<{name}>\d{{1,4}}(?:[.,]\d{{1,2}})?|{Alt(Numbers.Keys)}{R})";
            string Unit(string name) => $"(?<{name}>{Alt(Units.Keys)}){R}";

            string Date(string p)
            {
                var months = $"{Alt(Months.Keys)}{R}";
                var joiner = Alt(pack.MonthJoiners);
                var parts = new List<string>
                {
                    $"(?<{p}dat>{Alt(pack.DayAfterTomorrow)}){R}",
                    $"(?<{p}tod>{Alt(pack.Today)}){R}",
                    $"(?<{p}tom>{Alt(pack.Tomorrow)}){R}",
                    $@"{Alt(pack.InPrefix)}\s+(?:{Number(p + "inn")}\s*)?{Unit(p + "inu")}",
                    $@"(?:(?<{p}np>{Alt(pack.NextPrefix)})\s+)?(?:{on}\s+)?(?<{p}wd>{weekday})(?:\s+(?<{p}ns>{Alt(pack.NextSuffix)}){R})?",
                    $@"(?:{on}\s+)?(?<{p}d>\d{{1,2}})(?:{daySuffix})?\s*(?:{joiner}\s+)?(?<{p}m>{months})\.?(?:,?\s+(?:{joiner}\s+)?(?<{p}y>\d{{4}}){R})?",
                    $@"(?<{p}m2>{months})\.?\s*(?<{p}d2>\d{{1,2}})(?:{daySuffix})?{R}(?:,?\s+(?<{p}y2>\d{{4}}){R})?",
                    $@"(?<{p}iy>\d{{4}})-(?<{p}im>\d{{1,2}})-(?<{p}id>\d{{1,2}})",
                    $@"(?<{p}na>\d{{1,2}})/(?<{p}nb>\d{{1,2}})(?:/(?<{p}nc>\d{{4}}|\d{{2}}))?",
                    $@"(?<{p}na2>\d{{1,2}})\.(?<{p}nb2>\d{{1,2}})\.(?<{p}nc2>\d{{4}})",
                };
                if (pack.InSuffix.Length > 0) parts.Insert(4, $@"{Number(p + "isn")}\s*{Unit(p + "isu")}\s+{Alt(pack.InSuffix)}{R}");
                return "(?:" + string.Join("|", parts) + ")";
            }

            string Time(string p)
            {
                var at = Alt(pack.AtMarkers);
                var meridiem = Alt([.. pack.Am, .. pack.Pm, .. pack.Midday]);
                return "(?:" + string.Join("|",
                    $@"(?:{at}\s+)?(?<{p}h>\d{{1,2}})(?:[:.](?<{p}mi>\d{{2}}))?\s*(?<{p}ap>{meridiem}){R}",
                    $@"(?:{at}\s+)?(?<{p}h2>\d{{1,2}}):(?<{p}mi2>\d{{2}})(?![\d:])",
                    $@"(?:{at}\s+)?(?<{p}h3>\d{{1,2}})h(?<{p}mi3>\d{{2}}){R}",
                    $@"{at}\s*(?<{p}h4>\d{{1,2}})(?:[.:h](?<{p}mi4>\d{{2}}))?h?{R}",
                    $@"(?:{at}\s+)?(?<{p}noon>{Alt(pack.Noon)}){R}",
                    $@"(?:{at}\s+)?(?<{p}mid>{Alt(pack.Midnight)}){R}") + ")";
            }
        }

        public LanguagePack Pack { get; }
        public string Code { get; }
        public Dictionary<string, int> Numbers { get; }
        public Dictionary<string, UnitWord> Units { get; }
        public Dictionary<string, DayOfWeek> Weekdays { get; }
        public HashSet<string> Abbreviations { get; }
        public Dictionary<string, int> Months { get; }
        public Dictionary<string, (RecurrenceFrequency Frequency, int Interval)> Adverbs { get; }
        public Dictionary<string, TaskPriority> Priorities { get; }
        public HashSet<string> Pm { get; }
        public HashSet<string> Midday { get; }
        public HashSet<string> PersonalSpace { get; }
        public Regex OnMarkerPattern { get; }

        public Regex For(Phase phase) => _patterns[phase];

        public IReadOnlyList<DayOfWeek> ReadWeekdays(string foldedList) =>
            [.. _weekdayItem.Matches(foldedList).Select(item => Weekdays[Key(item.Value)]).Distinct().Order()];

        private static string Folded(string word) => Key(FoldedText.FoldWord(word));

        private static Dictionary<string, T> Fold<T>(IReadOnlyDictionary<string, T> source)
        {
            var result = new Dictionary<string, T>(StringComparer.Ordinal);
            foreach (var (word, value) in source) result.TryAdd(Folded(word), value);
            return result;
        }

        private static string Alt(IEnumerable<string> words)
        {
            var folded = words.Select(Folded).Where(word => word.Length > 0).Distinct(StringComparer.Ordinal).OrderByDescending(word => word.Length).ToList();
            return folded.Count == 0 ? "(?!)" : "(?:" + string.Join("|", folded.Select(word => Regex.Escape(word).Replace(@"\ ", @"\s+"))) + ")";
        }

        private static Regex Compile(string pattern) => new(pattern, RegexOptions.ExplicitCapture | RegexOptions.CultureInvariant, MatchTimeout);
    }
}

/// <summary>Local wall time to an instant (PLAN §6): a time in a DST gap moves forward by the gap; an ambiguous time takes the earlier instant.</summary>
public static class LocalInstant
{
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            var before = zone.GetUtcOffset(local.AddHours(-12));
            return new DateTimeOffset(local, before).ToUniversalTime();
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

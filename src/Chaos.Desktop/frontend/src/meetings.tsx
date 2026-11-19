import React from "react";
import { calendarsRpc, markdownRpc, type CalendarMeetingDto, type CalendarTaskLinkDto } from "#neoastra";
import { Video } from "lucide-react";
import { minutesText } from "./MyWeek";

/** "10:00–11:00", or "All day". */
export const meetingTime = (meeting: CalendarMeetingDto) => (meeting.allDay ? "All day" : `${meeting.start ?? ""}–${meeting.end ?? ""}`);

/** The link to open for a meeting: its video call first, then its page; https only. */
export const meetingLink = (meeting: CalendarMeetingDto) =>
  [meeting.videoUrl, meeting.webUrl].find((url) => !!url && url.startsWith("https://")) ?? null;

/** All-day meetings first, then by start time. */
export const sortMeetings = (meetings: readonly CalendarMeetingDto[]) =>
  [...meetings].sort(
    (a, b) => Number(b.allDay) - Number(a.allDay) || (a.start ?? "").localeCompare(b.start ?? "") || a.title.localeCompare(b.title),
  );

/** "2 h meetings" for a day's busy time. */
export const busyText = (minutes: number) => `${minutesText(minutes)} meetings`;

/**
 * Meetings of one day, read-only (PLAN2 §10.4): in their calendar's colour, lighter when they do not
 * count as busy time. A link opens in the browser through the host, never inside the app.
 */
export function MeetingsToday({
  meetings,
  busyMinutes,
  freeMinutes,
  doNotFit,
  remainingMinutes,
  onOpenLink = (url) => void markdownRpc.openExternal({ url }),
}: {
  meetings: readonly CalendarMeetingDto[];
  busyMinutes: number;
  freeMinutes: number;
  doNotFit: boolean;
  remainingMinutes: number;
  onOpenLink?: (url: string) => void;
}) {
  if (meetings.length === 0) return null;
  return (
    <section aria-label="Meetings today" className="grid gap-1.5 rounded-xl border bg-card p-3">
      <h3 className="flex flex-wrap items-baseline justify-between gap-x-3 text-sm font-semibold">
        Meetings today
        <span className="text-xs font-normal text-muted-foreground tabular-nums">
          {busyMinutes > 0 ? `${busyText(busyMinutes)} · ${minutesText(freeMinutes)} left for tasks` : "No busy time"}
        </span>
      </h3>
      {doNotFit && (
        <p role="status" className="text-sm text-destructive">
          Open tasks need {minutesText(remainingMinutes)} but only {minutesText(freeMinutes)} is free after meetings. Nothing was moved.
        </p>
      )}
      <ol className="grid gap-1">
        {sortMeetings(meetings).map((meeting) => {
          const link = meetingLink(meeting);
          return (
            <li
              key={meeting.id}
              data-meeting={meeting.id}
              className="flex min-w-0 items-center gap-2 rounded-md border-l-4 bg-muted/40 px-2 py-1 text-sm"
              style={{ borderLeftColor: meeting.color ?? undefined, opacity: meeting.busy ? 1 : 0.65 }}
            >
              <span className="w-24 shrink-0 text-xs tabular-nums text-muted-foreground">{meetingTime(meeting)}</span>
              <span className="min-w-0 flex-1 truncate">
                {meeting.title}
                {!meeting.busy && <span className="ml-1 text-xs text-muted-foreground">(free)</span>}
              </span>
              {link && (
                <button
                  type="button"
                  className="inline-flex items-center gap-1 text-xs text-primary hover:underline"
                  aria-label={`Open ${meeting.title}`}
                  onClick={() => onOpenLink(link)}
                >
                  <Video className="size-3.5" />
                  {meeting.videoUrl ? "Join" : "Open"}
                </button>
              )}
            </li>
          );
        })}
      </ol>
    </section>
  );
}

/** "From meeting <title>, <time>" for a task made from a meeting, with a link that opens the meeting in the browser. */
export function MeetingTaskNotice({
  taskId,
  api = calendarsRpc,
  onOpenLink = (url) => void markdownRpc.openExternal({ url }),
}: {
  taskId: string;
  api?: Pick<typeof calendarsRpc, "taskLinks">;
  onOpenLink?: (url: string) => void;
}) {
  const [link, setLink] = React.useState<CalendarTaskLinkDto | null>(null);
  React.useEffect(() => {
    let current = true;
    setLink(null);
    void api
      .taskLinks({ taskIds: [taskId] })
      .then((result) => {
        if (current && result.ok) setLink(result.links.find((item) => item.taskId === taskId) ?? null);
      })
      .catch(() => {
        /* the line is optional */
      });
    return () => {
      current = false;
    };
  }, [api, taskId]);
  if (!link) return null;
  const time = new Date(link.eventStartUtc).toLocaleString([], { dateStyle: "medium", timeStyle: "short" });
  const url = link.eventWebUrl?.startsWith("https://") ? link.eventWebUrl : null;
  return (
    <p
      role="status"
      aria-label="Meeting this task came from"
      className="flex flex-wrap items-center gap-2 rounded-lg border bg-muted/50 px-3 py-2 text-xs"
    >
      <span dir="auto">
        From meeting {link.eventTitle}, {time}
      </span>
      {url && (
        <button type="button" className="w-fit underline" onClick={() => onOpenLink(url)}>
          Open meeting
        </button>
      )}
    </p>
  );
}

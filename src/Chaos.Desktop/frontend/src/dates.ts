import React from "react";

/** Whole days from `from` to `to`, both yyyy-MM-dd local dates (calendar arithmetic, no zones). */
export function daysBetween(from: string, to: string) {
  const [fy, fm, fd] = from.split("-").map(Number);
  const [ty, tm, td] = to.split("-").map(Number);
  return Math.round((Date.UTC(ty, tm - 1, td) - Date.UTC(fy, fm - 1, fd)) / 86_400_000);
}

const weekday = new Intl.DateTimeFormat(undefined, { weekday: "short", timeZone: "UTC" });
const dayMonth = new Intl.DateTimeFormat(undefined, { weekday: "short", day: "numeric", month: "short", timeZone: "UTC" });
const dayMonthYear = new Intl.DateTimeFormat(undefined, { day: "numeric", month: "short", year: "numeric", timeZone: "UTC" });
const asDate = (date: string) => new Date(`${date}T00:00:00Z`);

/**
 * A local date as people say it: Today, Tomorrow, Yesterday, a weekday within the coming
 * week, otherwise "Mon 30 Nov" (with the year when it is not this year).
 */
export function friendlyDate(date: string, today: string) {
  const offset = daysBetween(today, date);
  if (offset === 0) return "Today";
  if (offset === 1) return "Tomorrow";
  if (offset === -1) return "Yesterday";
  if (offset > 1 && offset < 7) return weekday.format(asDate(date));
  return date.slice(0, 4) === today.slice(0, 4) ? dayMonth.format(asDate(date)) : dayMonthYear.format(asDate(date));
}

/** How far away a date is, for a tooltip or a header: "in 17 days", "3 days ago". */
export function relativeDays(date: string, today: string) {
  const offset = daysBetween(today, date);
  if (offset === 0) return "today";
  if (offset === 1) return "tomorrow";
  if (offset === -1) return "yesterday";
  return offset > 0 ? `in ${offset} days` : `${-offset} days ago`;
}

export type DateTone = "overdue" | "soon" | "normal";

/** Due dates before today are overdue; due today or tomorrow is soon. Planned dates are never overdue. */
export function dueTone(date: string, today: string, kind: "due" | "planned" = "due"): DateTone {
  const offset = daysBetween(today, date);
  if (kind === "planned") return "normal";
  return offset < 0 ? "overdue" : offset <= 1 ? "soon" : "normal";
}

export const toneClass: Record<DateTone, string> = {
  overdue: "font-medium text-destructive",
  soon: "font-medium text-amber-600 dark:text-amber-400",
  normal: "",
};

/** "Due Tomorrow 15:00" style text with the exact date kept for the tooltip. */
export function dateLabel(prefix: string, date: string, time: string | null | undefined, today: string) {
  return `${prefix} ${friendlyDate(date, today)}${time ? ` ${time.slice(0, 5)}` : ""}`;
}

/** The profile's local today, provided by the app shell; falls back to this computer's date. */
export const TodayContext = React.createContext<string | null>(null);

export function useToday() {
  const today = React.useContext(TodayContext);
  if (today) return today;
  const now = new Date();
  return `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, "0")}-${String(now.getDate()).padStart(2, "0")}`;
}

/** A due or planned date as a span with its tone and the exact date in the tooltip. */
export function DateText({ kind, date, time, className, children }: { kind: "due" | "planned"; date: string; time?: string | null; className?: string; children?: React.ReactNode }) {
  const today = useToday();
  const tone = dueTone(date, today, kind);
  const label = dateLabel(kind === "due" ? "Due" : "Planned", date, time, today);
  return React.createElement("span", {
    className: ["inline-flex items-center gap-1", toneClass[tone], className].filter(Boolean).join(" "),
    title: `${kind === "due" ? "Due" : "Planned"} ${date}${time ? ` ${time.slice(0, 5)}` : ""} (${relativeDays(date, today)})${tone === "overdue" ? " · overdue" : ""}`,
  }, children, label);
}

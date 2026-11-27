/** The user's working week: the day it starts on and how many days it has (Egypt: Sunday, 5 days; most places: Monday, 5). */
export type WorkWeek = { start: number; days: number };

export const weekdayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"] as const;
export const defaultWorkWeek: WorkWeek = { start: 1, days: 5 };

const weekKey = "chaos.workWeek";
const onlyKey = "chaos.calendarWorkingDaysOnly";

export function loadWorkWeek(): WorkWeek {
  try {
    const parsed = JSON.parse(localStorage.getItem(weekKey) ?? "null") as Partial<WorkWeek> | null;
    const start = parsed?.start;
    const days = parsed?.days;
    if (Number.isInteger(start) && start! >= 0 && start! <= 6 && Number.isInteger(days) && days! >= 1 && days! <= 7)
      return { start: start!, days: days! };
  } catch {
    /* fall through to the default */
  }
  return defaultWorkWeek;
}

export function saveWorkWeek(week: WorkWeek) {
  try {
    localStorage.setItem(weekKey, JSON.stringify(week));
  } catch {
    /* storage unavailable */
  }
}

export function loadWorkingDaysOnly(): boolean {
  try {
    return localStorage.getItem(onlyKey) === "1";
  } catch {
    return false;
  }
}

export function saveWorkingDaysOnly(value: boolean) {
  try {
    localStorage.setItem(onlyKey, value ? "1" : "0");
  } catch {
    /* storage unavailable */
  }
}

/** Weekday numbers (0 = Sunday) that are not working days: the days after the working week's last day. */
export function nonWorkingDays(week: WorkWeek): number[] {
  const off: number[] = [];
  for (let offset = week.days; offset < 7; offset++) off.push((week.start + offset) % 7);
  return off;
}

/** The working days in order, starting at the week's first day. */
export function workingDayNames(week: WorkWeek): string[] {
  return Array.from({ length: week.days }, (_, offset) => weekdayNames[(week.start + offset) % 7]);
}

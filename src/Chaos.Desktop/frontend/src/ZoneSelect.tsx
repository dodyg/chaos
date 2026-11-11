import React from "react";
import { shellRpc } from "#neoastra";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";

type ZoneApi = Pick<typeof shellRpc, "settings" | "zones">;
export type TimeZones = { zones: readonly string[]; preferred: string };
/** Used when no zone list was passed in: just this computer's zone. */
export const deviceTimeZones = (): TimeZones => ({ zones: [], preferred: Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC" });

/** The device's time zones and the profile's own zone from Settings, used to default zone pickers. */
export function useTimeZones(api: ZoneApi = shellRpc, refreshKey: unknown = null): TimeZones {
  const [zones, setZones] = React.useState<string[]>([]);
  const [preferred, setPreferred] = React.useState<string | null>(null);
  React.useEffect(() => {
    let alive = true;
    void Promise.all([api.settings({}), api.zones({})]).then(([settings, list]) => {
      if (!alive) return;
      if (settings.ok && settings.zoneId) setPreferred(settings.zoneId);
      setZones([...list.zones]);
    }).catch(() => { /* the picker still offers the current and fallback zones */ });
    return () => { alive = false; };
  }, [api, refreshKey]);
  return { zones, preferred: preferred ?? (Intl.DateTimeFormat().resolvedOptions().timeZone || "UTC") };
}

/**
 * A time zone drop-down. An empty value shows the preferred zone from Settings, which is what a
 * new time is saved with.
 */
export function ZoneSelect({ value, preferred, zones, onChange, disabled, id }: { value: string | null | undefined; preferred: string; zones: readonly string[]; onChange: (zone: string) => void; disabled?: boolean; id?: string }) {
  const shown = value || preferred;
  const options = [...new Set([shown, preferred, ...zones])];
  return <NativeSelect id={id} className="w-full" value={shown} disabled={disabled} onChange={e => onChange(e.target.value)}>
    {options.map(zone => <NativeSelectOption key={zone} value={zone}>{zone.replaceAll("_", " ")}</NativeSelectOption>)}
  </NativeSelect>;
}

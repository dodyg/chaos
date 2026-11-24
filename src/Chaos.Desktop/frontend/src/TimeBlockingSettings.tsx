import React from "react";
import { timeBlockRpc, type TimeBlockStatusDto } from "#neoastra";
import { CalendarClock, RefreshCw, Unplug } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Field, InlineError } from "@/components/common";

type Api = Pick<typeof timeBlockRpc, "status" | "enable" | "update" | "disable" | "syncNow" | "runOnThisDevice" | "signIn">;

const when = (instant: string | null) => (instant ? new Date(instant).toLocaleString() : "never");

/** Which spaces are blocked: every space, or the ones the person ticked. Null means every space. */
function spaceSelection(status: TimeBlockStatusDto): { every: boolean; chosen: Set<string> } {
  const chosen = new Set(status.spaces.filter((space) => space.chosen).map((space) => space.id));
  return { every: status.spaces.every((space) => space.chosen), chosen };
}

function SpacesPicker({
  spaces,
  every,
  chosen,
  onEvery,
  onToggle,
  disabled,
}: {
  spaces: TimeBlockStatusDto["spaces"];
  every: boolean;
  chosen: Set<string>;
  onEvery: (every: boolean) => void;
  onToggle: (id: string, on: boolean) => void;
  disabled?: boolean;
}) {
  return (
    <fieldset className="grid gap-1.5" disabled={disabled} aria-label="Spaces to block">
      <legend className="text-sm font-medium">Spaces</legend>
      <label className="inline-flex items-center gap-1.5 text-sm">
        <input type="checkbox" className="accent-primary" checked={every} onChange={(event) => onEvery(event.target.checked)} />
        Every space
      </label>
      {/* An empty list means every space on the backend, so the last ticked space cannot be unticked. */}
      {!every && (
        <ul className="grid gap-1 pl-5">
          {spaces.map((space) => (
            <li key={space.id}>
              <label className="inline-flex items-center gap-1.5 text-sm">
                <input
                  type="checkbox"
                  className="accent-primary"
                  checked={chosen.has(space.id)}
                  disabled={chosen.has(space.id) && chosen.size === 1}
                  onChange={(event) => onToggle(space.id, event.target.checked)}
                />
                <span dir="auto">{space.name}</span>
              </label>
            </li>
          ))}
        </ul>
      )}
    </fieldset>
  );
}

/**
 * Settings → Calendars → Time blocking (PLAN3 §11): tasks with a planned time and an estimate become blocks in a
 * Google calendar the app creates, on one device of the person's choosing. Moving a block in Google moves the task;
 * deleting it clears the planned time. Nothing else in Google is ever written, and with the feature off no request is made.
 */
export function TimeBlockingSettings({ api = timeBlockRpc, onChanged }: { api?: Api; onChanged?: () => void }) {
  const [status, setStatus] = React.useState<TimeBlockStatusDto | null>(null);
  const [configured, setConfigured] = React.useState(true);
  const [error, setError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [accountId, setAccountId] = React.useState("");
  const [showTitles, setShowTitles] = React.useState(true);
  const [selection, setSelection] = React.useState<{ every: boolean; chosen: Set<string> }>({ every: true, chosen: new Set() });
  const [confirming, setConfirming] = React.useState(false);
  const [deleteCalendar, setDeleteCalendar] = React.useState(true);

  function apply(next: TimeBlockStatusDto) {
    setStatus(next);
    setShowTitles(next.showTitles);
    setSelection(spaceSelection(next));
    setAccountId(
      (current) => current || next.accountId || next.accounts.find((account) => account.signedInHere)?.id || next.accounts[0]?.id || "",
    );
  }

  React.useEffect(() => {
    let current = true;
    void api
      .status({})
      .then((result) => {
        if (!current) return;
        setConfigured(result.configured);
        if (result.ok && result.status) apply(result.status);
        else setError(result.error?.message ?? "Could not load time blocking.");
      })
      .catch((e: unknown) => {
        if (current) setError(String(e));
      });
    return () => {
      current = false;
    };
  }, [api]);

  async function run(
    work: () => Promise<{ ok: boolean; status?: TimeBlockStatusDto | null; error?: { message: string } | null }>,
    done?: string,
  ) {
    if (busy) return;
    setBusy(true);
    setError(null);
    setNotice(null);
    try {
      const result = await work();
      if (!result.ok || !result.status) {
        setError(result.error?.message ?? "Time blocking did not change.");
        return;
      }
      apply(result.status);
      if (done) setNotice(done);
      onChanged?.();
    } catch (e) {
      setError(String(e));
    } finally {
      setBusy(false);
    }
  }

  const spaces = selection.every ? null : [...selection.chosen];

  async function turnOn() {
    await run(
      () => api.enable({ accountId, showTitles, spaceIds: spaces }),
      "Time blocking is on. Your tasks appear in Google within a few minutes.",
    );
  }

  async function save(patch: { showTitles?: boolean; spaces?: Set<string>; every?: boolean }) {
    if (!status) return;
    const next = {
      showTitles: patch.showTitles ?? showTitles,
      every: patch.every ?? selection.every,
      chosen: patch.spaces ?? selection.chosen,
    };
    // No spaces would be sent as an empty list, which the backend reads as every space.
    if (!next.every && next.chosen.size === 0) return;
    setShowTitles(next.showTitles);
    setSelection({ every: next.every, chosen: next.chosen });
    await run(
      () =>
        api.update({ expectedVersion: status.profileVersion, showTitles: next.showTitles, spaceIds: next.every ? null : [...next.chosen] }),
      "Saved.",
    );
  }

  const signedIn = status?.accounts.filter((account) => account.signedInHere) ?? [];
  const canTurnOn = !!status && signedIn.some((account) => account.id === accountId) && (selection.every || selection.chosen.size > 0);

  return (
    <section aria-label="Time blocking" className="grid gap-3 rounded-xl border bg-card p-4">
      <div className="grid gap-0.5">
        <h3 className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground">
          <CalendarClock />
          Time blocking
        </h3>
        <p className="text-sm text-muted-foreground">
          Show tasks with a planned time and an estimate as blocks in a separate Google calendar named <strong>Chaos</strong>, which the app
          creates. Moving a block in Google moves the task&apos;s planned time; deleting the block clears the time. Chaos never writes to
          any other calendar, and a shared task counts only when it is assigned to you.
        </p>
      </div>
      {error && <InlineError>{error}</InlineError>}
      {notice && (
        <p role="status" className="text-sm text-muted-foreground">
          {notice}
        </p>
      )}
      {status === null ? (
        <p className="text-sm text-muted-foreground">Loading time blocking…</p>
      ) : !configured ? (
        <p className="text-sm text-muted-foreground">This build of Chaos has no Google client set up.</p>
      ) : !status.enabled ? (
        status.accounts.length === 0 ? (
          <p className="text-sm text-muted-foreground">Connect a Google account above to turn on time blocking.</p>
        ) : (
          <form
            className="grid gap-3"
            data-time-block="off"
            onSubmit={(event) => {
              event.preventDefault();
              void turnOn();
            }}
          >
            <Field label="Google account">
              <select
                id="time-block-account"
                className="rounded-md border bg-background px-2 py-1 text-sm"
                value={accountId}
                onChange={(event) => setAccountId(event.target.value)}
                disabled={busy}
              >
                {status.accounts.map((account) => (
                  <option key={account.id} value={account.id}>
                    {account.email}
                    {account.signedInHere ? "" : " (sign in on this device)"}
                  </option>
                ))}
              </select>
            </Field>
            <fieldset className="grid gap-1.5" aria-label="Block title">
              <legend className="text-sm font-medium">Block title</legend>
              <div className="flex flex-wrap gap-3 text-sm">
                <label className="inline-flex items-center gap-1.5">
                  <input
                    type="radio"
                    name="time-block-titles"
                    className="accent-primary"
                    checked={showTitles}
                    onChange={() => setShowTitles(true)}
                    disabled={busy}
                  />
                  Show task titles
                </label>
                <label className="inline-flex items-center gap-1.5">
                  <input
                    type="radio"
                    name="time-block-titles"
                    className="accent-primary"
                    checked={!showTitles}
                    onChange={() => setShowTitles(false)}
                    disabled={busy}
                  />
                  Show as Busy (no titles)
                </label>
              </div>
            </fieldset>
            <SpacesPicker
              spaces={status.spaces}
              every={selection.every}
              chosen={selection.chosen}
              disabled={busy}
              onEvery={(every) => setSelection((current) => ({ every, chosen: current.chosen }))}
              onToggle={(id, on) =>
                setSelection((current) => {
                  const chosen = new Set(current.chosen);
                  if (on) chosen.add(id);
                  else chosen.delete(id);
                  return { every: current.every, chosen };
                })
              }
            />
            <p className="text-xs text-muted-foreground">
              Google asks for one more permission the first time: to create that calendar and manage only its events. This device will be
              the one that keeps it up to date; you can move that to another device later.
            </p>
            <div>
              <Button type="submit" disabled={busy || !canTurnOn}>
                Turn on time blocking
              </Button>
            </div>
          </form>
        )
      ) : (
        <div className="grid gap-3" data-time-block="on">
          <dl className="grid gap-1 text-sm sm:grid-cols-[auto_1fr] sm:gap-x-3">
            <dt className="text-muted-foreground">Account</dt>
            <dd>{status.accountEmail}</dd>
            <dt className="text-muted-foreground">Calendar</dt>
            <dd>
              Chaos (created by this app; {status.eventCount} {status.eventCount === 1 ? "block" : "blocks"})
            </dd>
            <dt className="text-muted-foreground">Kept up to date</dt>
            <dd className="flex flex-wrap items-center gap-2">
              {status.isRunner ? "on this device" : "on another device"}
              {!status.isRunner && (
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={busy || !signedIn.some((account) => account.id === status.accountId)}
                  onClick={() =>
                    void run(
                      () => api.runOnThisDevice({ expectedVersion: status.profileVersion }),
                      "This device now keeps the calendar up to date.",
                    )
                  }
                >
                  Run on this device
                </Button>
              )}
            </dd>
            <dt className="text-muted-foreground">Last sync</dt>
            <dd>{when(status.lastPassUtc)}</dd>
          </dl>
          {status.needsSignIn && (
            <div role="alert" className="grid gap-2 text-sm">
              <p>This device can no longer update the calendar. Sign in again; Google asks only for the time blocking permission.</p>
              <div>
                <Button
                  type="button"
                  size="sm"
                  variant="outline"
                  disabled={busy}
                  onClick={() => void run(() => api.signIn({}), "Signed in. The calendar is up to date again.")}
                >
                  Sign in again
                </Button>
              </div>
            </div>
          )}
          {status.lastError && !status.needsSignIn && <InlineError>{status.lastError}</InlineError>}
          <p className="text-sm">
            <label className="inline-flex items-center gap-1.5">
              <input
                type="checkbox"
                className="accent-primary"
                checked={showTitles}
                disabled={busy}
                onChange={(event) => void save({ showTitles: event.target.checked })}
                aria-label="Show task titles"
              />
              Show task titles (off shows blocks as Busy)
            </label>
          </p>
          <SpacesPicker
            spaces={status.spaces}
            every={selection.every}
            chosen={selection.chosen}
            disabled={busy}
            onEvery={(every) => void save({ every, spaces: selection.chosen })}
            onToggle={(id, on) => {
              const chosen = new Set(selection.chosen);
              if (on) chosen.add(id);
              else chosen.delete(id);
              void save({ spaces: chosen });
            }}
          />
          <div className="flex flex-wrap gap-2">
            <Button
              type="button"
              size="sm"
              variant="outline"
              disabled={busy}
              onClick={() => void run(() => api.syncNow({}), "Synced with Google.")}
            >
              <RefreshCw />
              Sync now
            </Button>
            <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => setConfirming(true)}>
              <Unplug />
              Turn off…
            </Button>
          </div>
          {confirming && (
            <form
              className="grid gap-2 rounded-md border p-3"
              aria-label="Turn off time blocking"
              onSubmit={(event) => {
                event.preventDefault();
                setConfirming(false);
                void run(() => api.disable({ expectedVersion: status.profileVersion, deleteCalendar }), "Time blocking is off.");
              }}
            >
              <p className="text-sm">
                Your tasks stay as they are. The Chaos calendar in Google can be deleted too; Chaos then forgets it.
              </p>
              <label className="inline-flex items-center gap-1.5 text-sm">
                <input
                  type="checkbox"
                  className="accent-primary"
                  checked={deleteCalendar}
                  onChange={(event) => setDeleteCalendar(event.target.checked)}
                />
                Delete the Chaos calendar in Google
              </label>
              <div className="flex flex-wrap gap-2">
                <Button type="submit" size="sm" variant="destructive" disabled={busy}>
                  Turn off
                </Button>
                <Button type="button" size="sm" variant="outline" disabled={busy} onClick={() => setConfirming(false)}>
                  Cancel
                </Button>
              </div>
            </form>
          )}
        </div>
      )}
    </section>
  );
}

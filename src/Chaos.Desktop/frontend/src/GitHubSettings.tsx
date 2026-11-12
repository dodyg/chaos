import React from "react";
import { gitHubRpc as githubRpc, type GitHubStatusResponse, type GitHubCandidateDto, type GitHubRepositoryDto, type GitHubFilterDto, type GitHubTargetDto, type GitHubOperationResponse } from "#neoastra";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Banner, Field } from "@/components/common";

type Api = typeof githubRpc;
const emptyFilter: GitHubFilterDto = { labels: [], labelMode: "any", assignee: "any", milestone: null, noMilestone: false, includeClosedDays: 0 };
type Options = { targetContainerId: string; targetSectionId: string | null; filter: GitHubFilterDto; milestonesAsProjects: boolean };
function RepositoryOptions({ targets, repository, busy, onSave, onCancel }: { targets: ReadonlyArray<GitHubTargetDto>; repository?: GitHubRepositoryDto; busy: boolean; onSave: (options: Options) => void; onCancel: () => void }) {
  const [container, setContainer] = React.useState(repository?.targetContainerId ?? targets[0]?.id ?? "");
  const [section, setSection] = React.useState(repository?.targetSectionId ?? "");
  const [filter, setFilter] = React.useState(() => repository?.filter ?? emptyFilter);
  const [labels, setLabels] = React.useState(filter.labels.join(", "));
  const [projects, setProjects] = React.useState(repository?.milestonesAsProjects ?? false);
  return <form className="grid gap-3 rounded-lg border p-4" onSubmit={e => { e.preventDefault(); onSave({ targetContainerId: container, targetSectionId: section || null, filter: { ...filter, labels: labels.split(",").map(v => v.trim()).filter(Boolean) }, milestonesAsProjects: projects }); }}>
    <p className="text-sm text-muted-foreground">The destination applies to newly imported tasks. Existing tasks keep their location.</p>
    <div className="grid gap-3 sm:grid-cols-2">
      <Field label="Destination"><NativeSelect value={container} onChange={e => { setContainer(e.target.value); setSection(""); }}>{targets.map(t => <NativeSelectOption key={t.id} value={t.id}>{t.name}</NativeSelectOption>)}</NativeSelect></Field>
      <Field label="Section"><NativeSelect value={section} onChange={e => setSection(e.target.value)}><NativeSelectOption value="">No section</NativeSelectOption>{targets.find(t => t.id === container)?.sections.map(s => <NativeSelectOption key={s.id} value={s.id}>{s.title}</NativeSelectOption>)}</NativeSelect></Field>
      <Field label="Labels (comma separated)"><Input value={labels} onChange={e => setLabels(e.target.value)}/></Field>
      <Field label="Label matching"><NativeSelect value={filter.labelMode} onChange={e => setFilter({ ...filter, labelMode: e.target.value })}><NativeSelectOption value="any">Any label</NativeSelectOption><NativeSelectOption value="all">All labels</NativeSelectOption></NativeSelect></Field>
      <Field label="Assigned to"><NativeSelect value={filter.assignee} onChange={e => setFilter({ ...filter, assignee: e.target.value })}><NativeSelectOption value="any">Anyone</NativeSelectOption><NativeSelectOption value="me">Me</NativeSelectOption><NativeSelectOption value="none">Unassigned</NativeSelectOption></NativeSelect></Field>
      <Field label="Milestone number (optional)"><Input type="number" min="1" step="1" disabled={filter.noMilestone} value={filter.milestone ?? ""} onChange={e => setFilter({ ...filter, milestone: e.target.value || null })}/></Field>
      <Field label="Include issues closed in the last days"><Input type="number" min="0" max="365" value={filter.includeClosedDays} onChange={e => setFilter({ ...filter, includeClosedDays: Number(e.target.value) })}/></Field>
    </div>
    <label className="flex gap-2 text-sm"><input type="checkbox" checked={filter.noMilestone} onChange={e => setFilter({ ...filter, noMilestone: e.target.checked, milestone: null })}/>Only issues without a milestone</label>
    <label className="flex gap-2 text-sm"><input type="checkbox" checked={projects} onChange={e => setProjects(e.target.checked)}/>Use milestones as projects</label>
    <div className="flex gap-2"><Button disabled={busy || !container}>Save repository</Button><Button type="button" variant="outline" disabled={busy} onClick={onCancel}>Cancel</Button></div>
  </form>;
}

export function GitHubSettings({ api = githubRpc }: { api?: Api }) {
  const pending = React.useRef(new Map<string, string>());
  const [status, setStatus] = React.useState<GitHubStatusResponse | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);
  const [candidates, setCandidates] = React.useState<ReadonlyArray<GitHubCandidateDto>>([]);
  const [cursor, setCursor] = React.useState<string | null>(null);
  const [lookup, setLookup] = React.useState("");
  const [chosen, setChosen] = React.useState<GitHubCandidateDto | null>(null);
  const [editing, setEditing] = React.useState<GitHubRepositoryDto | null>(null);
  const [confirm, setConfirm] = React.useState<GitHubRepositoryDto | "disconnect" | null>(null);
  async function load() { const result = await api.status({}); if (!result.ok) throw new Error(result.error?.message ?? "Could not load GitHub settings."); setStatus(result); }
  React.useEffect(() => { void load().catch(() => setError("Could not load GitHub settings.")); }, [api]);
  async function run(action: () => Promise<void>) { if (busy) return; setBusy(true); setError(null); try { await action(); } catch (e) { setError(e instanceof Error ? e.message : "Could not complete the request."); } finally { setBusy(false); } }
  async function retry<T extends { operationId: string }>(method: string, send: (request: T) => Promise<GitHubOperationResponse>, request: T) {
    const { operationId: unused, ...payload } = request;
    const key = method + JSON.stringify(payload);
    const operationId = pending.current.get(key) ?? request.operationId;
    pending.current.set(key, operationId);
    const result = await send({ ...request, operationId });
    if (result.ok) pending.current.delete(key);
    return result;
  }
  async function change(action: () => Promise<GitHubOperationResponse>) { const result = await action(); if (!result.ok) throw new Error(result.error?.message ?? "Could not save GitHub settings."); await load(); }
  async function discover(next: boolean) {
    await run(async () => { const result = await api.listRepositories({ cursor: next ? cursor : null }); if (!result.ok) throw new Error(result.error?.message ?? "Could not list repositories."); setCandidates(next ? [...candidates, ...result.repositories] : result.repositories); setCursor(result.nextCursor); });
  }
  const connection = status?.connection;
  const versioned = (r: { id: string; version: string }, paused = false) => ({ operationId: crypto.randomUUID(), id: r.id, expectedVersion: r.version, paused });
  return <section aria-label="GitHub connection" className="grid gap-4">
    <div><h2 className="text-lg font-semibold">GitHub</h2><p className="text-sm text-muted-foreground">Connect GitHub issues to your personal tasks. GitHub CLI keeps your login on this computer.</p></div>
    <Button variant="outline" className="justify-self-start" disabled={busy} onClick={() => void run(load)}>Reload settings</Button>
    {error && <Banner tone="error" onDismiss={() => setError(null)}>{error}</Banner>}
    {!status ? <p>Loading GitHub settings…</p> : !connection ? <div className="grid gap-3 rounded-xl border p-4">
      <p>Install GitHub CLI 2.103.3 or newer in its standard Program Files location. In a terminal, run <code>gh auth login --hostname github.com</code> and complete sign-in there, then return here.</p>
      <p className="text-sm text-muted-foreground">Connecting verifies the CLI account. Chaos uses its login in memory to call GitHub. Disconnecting leaves GitHub CLI signed in.</p>
      <Button className="justify-self-start" disabled={busy} onClick={() => void run(() => change(() => retry("connect", api.connect, { operationId: crypto.randomUUID() })))}>Connect using GitHub CLI</Button>
    </div> : <>
      <div className="flex flex-wrap items-center gap-3"><p>Connected as <strong>@{connection.accountLogin}</strong> · {connection.state}</p>
        <Button variant="outline" disabled={busy} onClick={() => void run(() => change(() => retry("pauseConnection", api.pauseConnection, versioned(connection, connection.state !== "Paused"))))}>{connection.state === "Paused" ? "Resume connection" : "Pause connection"}</Button>
        <Button variant="outline" disabled={busy} onClick={() => void run(() => change(() => retry("connect", api.connect, { operationId: crypto.randomUUID() })))}>Verify login</Button>
        <Button variant="outline" disabled={busy} onClick={() => setConfirm("disconnect")}>Disconnect</Button>
      </div>
      <label className="flex gap-2 text-sm"><input type="checkbox" disabled={busy} checked={connection.postDiaryByDefault} onChange={e => void run(() => change(() => retry("updateSettings", api.updateSettings, { ...versioned(connection), postDiaryByDefault: e.target.checked, createLabelsForTags: connection.createLabelsForTags })))}/>Post new diary updates to GitHub by default</label>
      <label className="flex gap-2 text-sm"><input type="checkbox" disabled={busy} checked={connection.createLabelsForTags} onChange={e => void run(() => change(() => retry("updateSettings", api.updateSettings, { ...versioned(connection), postDiaryByDefault: connection.postDiaryByDefault, createLabelsForTags: e.target.checked })))}/>Allow creating GitHub labels for local tags</label>
      <div className="grid gap-3"><h3 className="font-semibold">Repositories</h3>{status.repositories.length === 0 && <p className="text-sm text-muted-foreground">No repositories added yet.</p>}
        {status.repositories.map(r => <div key={r.id} className="grid gap-2 rounded-xl border p-3"><div className="flex flex-wrap items-center gap-2"><strong className="mr-auto">{r.fullName}</strong><span className="text-sm">{r.state}</span>
          <Button variant="outline" size="sm" disabled={busy} onClick={() => { setEditing(r); setChosen(null); }}>Edit repository</Button>
          <Button variant="outline" size="sm" disabled={busy} onClick={() => void run(() => change(() => retry("pauseRepository", api.pauseRepository, versioned(r, r.state !== "Paused"))))}>{r.state === "Paused" ? "Resume" : "Pause"}</Button>
          <Button variant="outline" size="sm" disabled={busy} onClick={() => setConfirm(r)}>Remove</Button></div>
          {editing?.id === r.id && <RepositoryOptions key={r.version} targets={status.targets} repository={r} busy={busy} onCancel={() => setEditing(null)} onSave={options => void run(async () => { await change(() => retry("updateRepository", api.updateRepository, { operationId: crypto.randomUUID(), id: r.id, expectedVersion: r.version, ...options })); setEditing(null); })}/>}
        </div>)}
      </div>
      <div className="grid gap-3 rounded-xl border p-4"><h3 className="font-semibold">Add a repository</h3><p className="text-sm text-muted-foreground">Repository visibility does not guarantee permission to update issues.</p>
        <Button variant="outline" className="justify-self-start" disabled={busy} onClick={() => void discover(false)}>Browse repositories</Button>
        <form className="flex items-end gap-2" onSubmit={e => { e.preventDefault(); void run(async () => { const parts = lookup.trim().split("/"); if (parts.length !== 2) throw new Error("Enter owner/repository."); const result = await api.lookupRepository({ owner: parts[0], name: parts[1] }); if (!result.ok) throw new Error(result.error?.message ?? "Repository not found."); setCandidates(result.repositories); setCursor(null); }); }}><Field label="Find owner/repository"><Input value={lookup} onChange={e => setLookup(e.target.value)} placeholder="owner/repository"/></Field><Button variant="outline" disabled={busy || !lookup.trim()}>Find</Button></form>
        {candidates.map(c => { const added = c.alreadyAdded || status.repositories.some(r => r.repositoryId === c.repositoryId); return <div key={c.repositoryId} className="flex items-center gap-2"><span className="mr-auto">{c.fullName}{c.isPrivate ? " · Private" : ""}</span><Button variant="outline" size="sm" disabled={busy || added || !c.hasIssues} onClick={() => { setChosen(c); setEditing(null); }}>{added ? "Already added" : !c.hasIssues ? "Issues disabled" : "Choose"}</Button></div>; })}
        {cursor && <Button variant="outline" disabled={busy} onClick={() => void discover(true)}>More repositories</Button>}
        {chosen && <div className="grid gap-2"><p>Adding {chosen.fullName}</p><RepositoryOptions key={chosen.repositoryId} targets={status.targets} busy={busy} onCancel={() => setChosen(null)} onSave={options => void run(async () => { await change(() => retry("addRepository", api.addRepository, { operationId: crypto.randomUUID(), connectionId: connection.id, expectedVersion: connection.version, repositoryId: chosen.repositoryId, owner: chosen.owner, name: chosen.name, ...options })); setChosen(null); })}/></div>}
      </div>
      {confirm && <div role="alertdialog" aria-label={confirm === "disconnect" ? "Disconnect GitHub?" : "Remove repository?"} className="grid gap-3 rounded-xl border p-4">
        <p>{confirm === "disconnect" ? "Disconnect GitHub? Your tasks stay in Chaos as ordinary tasks. GitHub CLI remains signed in." : `Remove ${confirm.fullName}? Its tasks stay in Chaos as ordinary tasks.`}</p>
        <div className="flex gap-2"><Button disabled={busy} onClick={() => void run(async () => { await change(() => confirm === "disconnect" ? retry("disconnect", api.disconnect, versioned(connection)) : retry("removeRepository", api.removeRepository, versioned(confirm))); setConfirm(null); setCandidates([]); setChosen(null); setEditing(null); })}>Confirm {confirm === "disconnect" ? "disconnect" : "removal"}</Button><Button variant="outline" disabled={busy} onClick={() => setConfirm(null)}>Cancel</Button></div>
      </div>}
    </>}
  </section>;
}

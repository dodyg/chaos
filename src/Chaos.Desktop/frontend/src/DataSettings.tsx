import React from "react";
import { dataRpc, type BackupDto, type BackupInspectResponse, type ContainerResponse, type CsvColumnDto, type DataCountsDto, type DataStatusResponse, type ImportPreviewResponse, type PurgeCountsDto } from "#neoastra";
import { Archive, Download, FolderOpen, HardDriveDownload, RotateCcw, Trash2, Upload } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { NativeSelect, NativeSelectOption } from "@/components/ui/native-select";
import { Banner, Count, Field, InlineError, PageHeader } from "@/components/common";

type Api = typeof dataRpc;
type ImportFormat = "csv" | "org" | "json" | "json-restore";

const reasonText: Record<string, string> = { Manual: "made by hand", Scheduled: "daily", PreImport: "before an import", PreRestore: "before a restore", PreMigration: "before an update" };
const formatText: Record<ImportFormat, string> = { csv: "CSV file", org: "Org file", json: "Chaos archive: add its tasks", "json-restore": "Chaos archive: replace all data" };
const fieldText: Record<string, string> = { title: "Title", notes: "Notes", list: "List", status: "Status", planned: "Planned date", due: "Due date", priority: "Priority", tags: "Tags", id: "Row ID", parent_id: "Parent row ID" };
const outcomeText: Record<string, string> = { New: "new", Duplicate: "already here", Conflict: "ID in use" };

export const resetPhrase = "DELETE";

export function when(iso: string | null) {
  return iso ? new Date(iso).toLocaleString(undefined, { dateStyle: "medium", timeStyle: "short" }) : "";
}

export function sizeText(bytes: number) {
  return bytes < 1024 * 1024 ? `${Math.max(1, Math.round(bytes / 1024))} KB` : `${(bytes / (1024 * 1024)).toFixed(1)} MB`;
}

export function countsText(counts: DataCountsDto | null) {
  if (!counts) return "";
  const n = (value: number, one: string, many: string) => `${value} ${value === 1 ? one : many}`;
  return `${n(counts.tasks, "task", "tasks")} (${counts.openTasks} open) · ${n(counts.lists, "list", "lists")} · ${n(counts.diaryEntries, "diary entry", "diary entries")} · ${n(counts.padItems, "pad note", "pad notes")}`;
}

/** Reads a chosen file as base64 for the import RPC (the file never leaves this computer). */
export async function fileToBase64(file: Blob) {
  const bytes = new Uint8Array(await file.arrayBuffer());
  let binary = "";
  for (let index = 0; index < bytes.length; index += 0x8000) binary += String.fromCharCode(...bytes.subarray(index, index + 0x8000));
  return btoa(binary);
}

function Section({ title, icon, children, description }: { title: string; icon: React.ReactNode; description: React.ReactNode; children: React.ReactNode }) {
  return <section aria-label={title} className="grid gap-3 rounded-xl border bg-card p-4">
    <div className="grid gap-0.5">
      <h3 className="flex items-center gap-2 text-base font-semibold [&_svg]:size-4 [&_svg]:text-muted-foreground">{icon}{title}</h3>
      <p className="text-sm text-muted-foreground">{description}</p>
    </div>
    {children}
  </section>;
}

/**
 * Settings → Data (PLAN §14): backups and restore, export, import with a preview, removing
 * deleted items for good and deleting all local data. Every destructive step shows what it will
 * do first and asks for a separate confirmation.
 */
export function DataSettings({ api = dataRpc, containers, onImported, onReplaced }: { api?: Api; containers: ContainerResponse[]; onImported: () => void; onReplaced: () => void }) {
  const [status, setStatus] = React.useState<DataStatusResponse | null>(null);
  const [error, setError] = React.useState<string | null>(null);
  const [notice, setNotice] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  const [review, setReview] = React.useState<{ backup: BackupDto; inspection: BackupInspectResponse } | null>(null);
  const [restoreConfirmed, setRestoreConfirmed] = React.useState(false);

  const [exportFormat, setExportFormat] = React.useState("json");
  const [exportDirectory, setExportDirectory] = React.useState("");
  const [exported, setExported] = React.useState<string | null>(null);

  const [importFormat, setImportFormat] = React.useState<ImportFormat>("csv");
  const [importFile, setImportFile] = React.useState<{ name: string; content: string } | null>(null);
  const [mapping, setMapping] = React.useState<CsvColumnDto[] | null>(null);
  const [preview, setPreview] = React.useState<ImportPreviewResponse | null>(null);
  const [listTargets, setListTargets] = React.useState<Record<string, string>>({});
  const [defaultContainer, setDefaultContainer] = React.useState("");
  const [replaceConfirmed, setReplaceConfirmed] = React.useState(false);

  const [purgeCounts, setPurgeCounts] = React.useState<PurgeCountsDto | null>(null);
  const [phrase, setPhrase] = React.useState("");
  const [deleteBackups, setDeleteBackups] = React.useState(false);

  const load = React.useCallback(async () => {
    const response = await api.status({});
    if (!response.ok) { setError(response.error?.message ?? "Could not read the data settings."); return; }
    setStatus(response);
    setExportDirectory(current => current || response.defaultExportDirectory || "");
  }, [api]);
  React.useEffect(() => { void load(); }, [load]);

  async function run(action: () => Promise<void>) {
    setBusy(true); setError(null); setNotice(null);
    try { await action(); } finally { setBusy(false); }
  }

  const backupNow = () => run(async () => {
    const response = await api.backupNow({});
    if (!response.ok) { setError(response.error?.message ?? "The backup failed."); await load(); return; }
    setNotice(`Backup made: ${countsText(response.backup!.counts)}.`);
    await load();
  });

  const reviewBackup = (backup: BackupDto) => run(async () => {
    const inspection = await api.inspectBackup({ fileName: backup.fileName });
    if (!inspection.ok) { setError(inspection.error?.message ?? "The backup could not be checked."); return; }
    setRestoreConfirmed(false);
    setReview({ backup, inspection });
  });

  const restore = () => run(async () => {
    if (!review?.inspection.checksum) return;
    const response = await api.restoreBackup({ fileName: review.backup.fileName, checksum: review.inspection.checksum });
    if (!response.ok) { setError(response.error?.message ?? "The restore failed; your data was kept."); return; }
    setReview(null);
    setNotice(`Restored. Your data from before the restore is in the backup ${response.safetyBackupFileName}.`);
    onReplaced();
    await load();
  });

  const runExport = () => run(async () => {
    const response = await api.export({ format: exportFormat, directory: exportDirectory || null });
    if (!response.ok) { setError(response.error?.message ?? "The export failed."); return; }
    setExported(response.path);
  });

  const openFolder = (which: "backups" | "exports") => run(async () => {
    const response = await api.openFolder({ which, path: null });
    if (!response.ok) setError(response.error?.message ?? "The folder could not be opened.");
  });

  async function previewImport(format: ImportFormat, file: { name: string; content: string }, columns: CsvColumnDto[] | null) {
    const response = await api.importPreview({ format, fileName: file.name, contentBase64: file.content, mapping: columns });
    if (!response.ok) { setPreview(null); setError(response.error?.message ?? "The file could not be read."); return; }
    setPreview(response);
    setMapping(format === "csv" ? [...response.csvMapping] : null);
    setListTargets(Object.fromEntries(response.lists.filter(list => list.existingContainerId).map(list => [list.sourceName, list.existingContainerId!])));
  }

  const chooseFile = (event: React.ChangeEvent<HTMLInputElement>) => {
    const chosen = event.target.files?.[0];
    if (!chosen) return;
    void run(async () => {
      const file = { name: chosen.name, content: await fileToBase64(chosen) };
      setImportFile(file); setReplaceConfirmed(false);
      await previewImport(importFormat, file, null);
    });
  };

  const changeColumn = (field: string, column: string) => {
    if (!importFile) return;
    const next = [...(mapping ?? []).filter(item => item.field !== field), ...(column === "" ? [] : [{ field, column: Number(column) }])];
    void run(() => previewImport(importFormat, importFile, next));
  };

  const commitImport = () => run(async () => {
    if (!importFile || !preview?.token) return;
    const response = await api.importCommit({
      format: importFormat, fileName: importFile.name, contentBase64: importFile.content, mapping, token: preview.token,
      listTargets: Object.entries(listTargets).filter(([, id]) => id).map(([sourceName, containerId]) => ({ sourceName, containerId })),
      defaultContainerId: defaultContainer || null,
    });
    if (!response.ok) { setError(response.error?.message ?? "Nothing was imported."); return; }
    setPreview(null); setImportFile(null);
    if (response.restore) {
      setNotice(`Restored from the archive. Your data from before is in the backup ${response.backupFileName}.`);
      onReplaced();
    } else {
      setNotice(`Imported ${response.created} ${response.created === 1 ? "task" : "tasks"}${response.duplicates ? `, skipped ${response.duplicates} already here` : ""}${response.conflicts ? `, kept ${response.conflicts} local ${response.conflicts === 1 ? "task" : "tasks"} with the same ID` : ""}. To undo, restore the backup ${response.backupFileName}.`);
      onImported();
    }

    await load();
  });

  const reviewPurge = () => run(async () => {
    const response = await api.purgePreview({ taskId: null });
    if (!response.ok) { setError(response.error?.message ?? "Deleted items could not be counted."); return; }
    setPurgeCounts(response.counts);
  });

  const purge = () => run(async () => {
    const response = await api.purge({ taskId: null });
    if (!response.ok) { setError(response.error?.message ?? "Deleted items could not be removed."); return; }
    setPurgeCounts(null);
    setNotice(`Removed ${response.counts!.total} deleted ${response.counts!.total === 1 ? "item" : "items"} permanently.`);
    onImported();
  });

  const reset = () => run(async () => {
    const response = await api.reset({ confirmation: phrase, deleteBackups });
    if (!response.ok) { setError(response.error?.message ?? "Local data could not be deleted."); return; }
    setPhrase("");
    setNotice(deleteBackups ? `All local data and ${response.backupFilesDeleted} backup files were deleted.` : "All local data was deleted. Backups were kept.");
    onReplaced();
    await load();
  });

  const userContainers = containers.filter(container => !container.archived);
  const latest = status?.latestBackup;

  return <div className="grid max-w-3xl gap-5">
    <PageHeader eyebrow="Settings" title="Data and backups" subtitle="Everything stays on this computer. Backups are ordinary local files; they are not encrypted."/>
    {status?.recoveryNotice && <Banner tone="warning">{status.recoveryNotice}</Banner>}
    {error && <Banner tone="error" onDismiss={() => setError(null)}>{error}</Banner>}
    {notice && <p role="status" className="rounded-lg border bg-muted/40 px-3 py-2 text-sm">{notice}</p>}

    <Section title="Backups" icon={<HardDriveDownload/>} description={`Chaos backs up at least once a day after changes, and before updates, imports and restores. It keeps one backup for each of the last ${status?.dailyKept ?? 7} days and ${status?.weeklyKept ?? 4} weeks.`}>
      <p className="text-sm">{latest ? <>Latest backup: <strong>{when(latest.createdAtUtc)}</strong> ({reasonText[latest.reason ?? ""] ?? latest.reason}), {countsText(latest.counts)}.</> : "No backup has been made yet."}</p>
      {status?.lastBackupError && <InlineError>The last backup failed ({when(status.lastBackupErrorAtUtc)}): {status.lastBackupError}</InlineError>}
      <div className="flex flex-wrap gap-2">
        <Button type="button" disabled={busy} onClick={() => void backupNow()}><HardDriveDownload/>Back up now</Button>
        <Button type="button" variant="outline" disabled={busy} onClick={() => void openFolder("backups")}><FolderOpen/>Open backups folder</Button>
      </div>
      {status && status.backups.length > 0 && <details>
        <summary className="cursor-pointer text-sm text-muted-foreground">All backups<Count>{status.backups.length}</Count></summary>
        <ul className="grid gap-1 pt-2">{status.backups.map(backup => <li key={backup.fileName} className="flex flex-wrap items-center justify-between gap-2 rounded-md px-2 py-1 hover:bg-accent">
          <span className="text-sm">{when(backup.createdAtUtc)} · {backup.described ? reasonText[backup.reason ?? ""] ?? backup.reason : "not checked yet"} · {sizeText(backup.sizeBytes)}</span>
          <Button type="button" size="xs" variant="outline" disabled={busy} onClick={() => void reviewBackup(backup)}><RotateCcw/>Review restore</Button>
        </li>)}</ul>
      </details>}
      {review && <div role="region" aria-label="Restore review" className="grid gap-2 rounded-lg border p-3">
        <p className="text-sm font-medium">Backup from {when(review.inspection.createdAtUtc ?? review.backup.createdAtUtc)}</p>
        {review.inspection.canRestore ? <>
          <p className="text-sm">It holds {countsText(review.inspection.counts)}.</p>
          <p className="text-sm text-muted-foreground">Now: {countsText(review.inspection.current)}.</p>
          <label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={restoreConfirmed} onChange={event => setRestoreConfirmed(event.target.checked)}/>
            Replace everything in Chaos with this backup. A backup of the current data is made first.</label>
          <div className="flex gap-2">
            <Button type="button" variant="destructive" disabled={busy || !restoreConfirmed} onClick={() => void restore()}>Restore this backup</Button>
            <Button type="button" variant="ghost" onClick={() => setReview(null)}>Cancel</Button>
          </div>
        </> : <InlineError>This backup cannot be restored: {review.inspection.problem}</InlineError>}
      </div>}
    </Section>

    <Section title="Export" icon={<Download/>} description="The Chaos archive is a complete copy of everything, including history and plans. Markdown and Org are readable plain text with the original notes; they leave some data out.">
      <div className="grid gap-3 sm:grid-cols-[12rem_1fr]">
        <Field label="Format"><NativeSelect value={exportFormat} onChange={event => setExportFormat(event.target.value)}>
          <NativeSelectOption value="json">Chaos archive (.zip)</NativeSelectOption>
          <NativeSelectOption value="markdown">Markdown files (.zip)</NativeSelectOption>
          <NativeSelectOption value="org">Org file (.org)</NativeSelectOption>
        </NativeSelect></Field>
        <Field label="Save in folder"><Input value={exportDirectory} onChange={event => setExportDirectory(event.target.value)}/></Field>
      </div>
      <div className="flex flex-wrap gap-2">
        <Button type="button" disabled={busy} onClick={() => void runExport()}><Download/>Export</Button>
        {exported && <Button type="button" variant="outline" disabled={busy} onClick={() => void openFolder("exports")}><FolderOpen/>Show in folder</Button>}
      </div>
      {exported && <p role="status" className="text-sm">Saved as <span className="break-all font-mono text-xs">{exported}</span></p>}
    </Section>

    <Section title="Import" icon={<Upload/>} description="Nothing is saved until you review the preview and confirm. A backup is made first, so an import can be undone by restoring it.">
      <div className="grid gap-3 sm:grid-cols-[16rem_1fr]">
        <Field label="Import from"><NativeSelect value={importFormat} onChange={event => { setImportFormat(event.target.value as ImportFormat); setPreview(null); setImportFile(null); }}>
          {(Object.keys(formatText) as ImportFormat[]).map(format => <NativeSelectOption key={format} value={format}>{formatText[format]}</NativeSelectOption>)}
        </NativeSelect></Field>
        <Field label="File"><Input type="file" accept={importFormat === "csv" ? ".csv,text/csv" : importFormat === "org" ? ".org,text/plain" : ".zip"} disabled={busy} onChange={chooseFile}/></Field>
      </div>
      {preview && importFormat === "json-restore" && <div role="region" aria-label="Import preview" className="grid gap-2 rounded-lg border p-3">
        <p className="text-sm">Archive exported {when(preview.archive?.exportedAtUtc ?? null)} by Chaos {preview.archive?.appVersion}: {countsText(preview.archiveCounts)}.</p>
        <label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={replaceConfirmed} onChange={event => setReplaceConfirmed(event.target.checked)}/>
          Replace everything in Chaos with this archive. A backup of the current data is made first.</label>
        <Button type="button" variant="destructive" className="justify-self-start" disabled={busy || !replaceConfirmed} onClick={() => void commitImport()}>Replace all data</Button>
      </div>}
      {preview && importFormat !== "json-restore" && <div role="region" aria-label="Import preview" className="grid gap-3 rounded-lg border p-3">
        <p className="text-sm"><strong>{preview.newTasks}</strong> new {preview.newTasks === 1 ? "task" : "tasks"}, {preview.subtasks} subtasks, {preview.diaryEntries} diary entries
          {preview.duplicates > 0 && <>; {preview.duplicates} already here (skipped)</>}{preview.conflicts > 0 && <>; {preview.conflicts} with an ID already used by a different task (local kept)</>}.</p>
        {importFormat === "csv" && mapping && <fieldset className="grid gap-2 sm:grid-cols-3">
          <legend className="text-xs font-semibold text-muted-foreground uppercase">Columns</legend>
          {preview.csvFields.map(field => <Field key={field} label={fieldText[field] ?? field}>
            <NativeSelect value={String(mapping.find(item => item.field === field)?.column ?? "")} onChange={event => changeColumn(field, event.target.value)}>
              <NativeSelectOption value="">(not imported)</NativeSelectOption>
              {preview.csvHeaders.map((header, index) => <NativeSelectOption key={index} value={String(index)}>{header || `Column ${index + 1}`}</NativeSelectOption>)}
            </NativeSelect>
          </Field>)}
        </fieldset>}
        {preview.lists.length > 0 && <fieldset className="grid gap-2">
          <legend className="text-xs font-semibold text-muted-foreground uppercase">Lists</legend>
          {preview.lists.map(list => <Field key={list.sourceName} label={`“${list.sourceName}” (${list.taskCount})`}>
            <NativeSelect value={listTargets[list.sourceName] ?? ""} onChange={event => setListTargets(current => ({ ...current, [list.sourceName]: event.target.value }))}>
              <NativeSelectOption value="">Create a new list “{list.sourceName}”</NativeSelectOption>
              {userContainers.map(container => <NativeSelectOption key={container.id} value={container.id}>Add to {container.name}</NativeSelectOption>)}
            </NativeSelect>
          </Field>)}
        </fieldset>}
        <Field label="Tasks without a list go to"><NativeSelect value={defaultContainer} onChange={event => setDefaultContainer(event.target.value)}>
          <NativeSelectOption value="">Inbox</NativeSelectOption>
          {userContainers.filter(container => container.kind !== "Inbox").map(container => <NativeSelectOption key={container.id} value={container.id}>{container.name}</NativeSelectOption>)}
        </NativeSelect></Field>
        {preview.errors.length > 0 && <InlineError>
          {preview.errors.length} {preview.errors.length === 1 ? "problem needs" : "problems need"} fixing in the file before importing:
          <ul className="list-disc pl-5">{preview.errors.slice(0, 20).map((issue, index) => <li key={index}>{issue.line > 0 ? `Line ${issue.line}: ` : ""}{issue.message}</li>)}</ul>
        </InlineError>}
        {preview.warnings.length > 0 && <details>
          <summary className="cursor-pointer text-sm text-muted-foreground">Notes about this file<Count>{preview.warnings.length}</Count></summary>
          <ul className="list-disc pl-5 text-sm">{preview.warnings.slice(0, 50).map((issue, index) => <li key={index}>{issue.line > 0 ? `Line ${issue.line}: ` : ""}{issue.message}</li>)}</ul>
        </details>}
        {preview.items.length > 0 && <details>
          <summary className="cursor-pointer text-sm text-muted-foreground">Tasks in the file<Count>{preview.items.length}</Count></summary>
          <ul className="grid gap-0.5 pt-1 text-sm">{preview.items.map(item => <li key={`${item.line}-${item.title}`} className="flex justify-between gap-2">
            <span className="truncate">{item.title}{item.listName ? <span className="text-muted-foreground"> · {item.listName}</span> : null}</span>
            <span className="text-muted-foreground" title={item.detail ?? undefined}>{outcomeText[item.outcome] ?? item.outcome}</span>
          </li>)}</ul>
        </details>}
        <Button type="button" className="justify-self-start" disabled={busy || !preview.canCommit} onClick={() => void commitImport()}>
          <Upload/>Import {preview.newTasks} {preview.newTasks === 1 ? "task" : "tasks"}
        </Button>
      </div>}
      {status && status.imports.length > 0 && <details>
        <summary className="cursor-pointer text-sm text-muted-foreground">Recent imports<Count>{status.imports.length}</Count></summary>
        <ul className="grid gap-0.5 pt-1 text-sm">{status.imports.map(batch => <li key={batch.id}>
          {when(batch.importedAtUtc)} · {batch.sourceName}: {batch.created} added, {batch.skipped} skipped{batch.backupFileName ? <span className="text-muted-foreground"> · undo by restoring the backup from just before</span> : null}
        </li>)}</ul>
      </details>}
    </Section>

    <Section title="Deleted items" icon={<Archive/>} description="Deleting a task or list moves it to the trash, where it can be restored. Removing it permanently cannot be undone; backups made earlier still contain it until they are rotated out.">
      {purgeCounts === null
        ? <Button type="button" variant="outline" className="justify-self-start" disabled={busy} onClick={() => void reviewPurge()}>Review deleted items</Button>
        : purgeCounts.total === 0
          ? <p className="text-sm">There are no deleted items.</p>
          : <div className="grid gap-2">
            <p className="text-sm">{purgeCounts.tasks} tasks, {purgeCounts.subtasks} subtasks, {purgeCounts.diaryEntries} diary entries, {purgeCounts.padItems} pad notes, {purgeCounts.lists} lists, {purgeCounts.tags} tags and {purgeCounts.templates} templates are in the trash.</p>
            <Button type="button" variant="destructive" className="justify-self-start" disabled={busy} onClick={() => void purge()}><Trash2/>Remove {purgeCounts.total} permanently</Button>
          </div>}
    </Section>

    <Section title="Delete all local data" icon={<Trash2/>} description="Stops reminders and removes every task, list, note, plan and setting from this computer. Exports you saved elsewhere are not touched. Windows may keep past notifications in its own history until you clear them there.">
      <label className="flex items-start gap-2 text-sm"><input type="checkbox" checked={deleteBackups} onChange={event => setDeleteBackups(event.target.checked)}/>Also delete Chaos's backups</label>
      <div className="flex flex-wrap items-end gap-2">
        <Field label={`Type ${resetPhrase} to confirm`}><Input value={phrase} autoComplete="off" onChange={event => setPhrase(event.target.value)}/></Field>
        <Button type="button" variant="destructive" disabled={busy || phrase !== resetPhrase} onClick={() => void reset()}>Delete all local data</Button>
      </div>
    </Section>
  </div>;
}

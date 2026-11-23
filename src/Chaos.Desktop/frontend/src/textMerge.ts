import { textMergeRpc } from "#neoastra";
import type { TextMerger } from "./padAutosave";

/** The host's three-way line merge (the same one the sync applier uses), so the rules are not written twice. */
export const mergeText: TextMerger = async (base, local, remote) => {
  const result = await textMergeRpc.merge3({ base, local, remote, localLabel: null, remoteLabel: null });
  return result.text;
};

/** The banner shown while a text still has marker lines from a merge. */
export const conflictNotice = "Some lines changed in two places; check the marked part.";

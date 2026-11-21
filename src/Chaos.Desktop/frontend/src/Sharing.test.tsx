import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { JSDOM } from "jsdom";
import React from "react";
import { containerRpc, spaceRpc, taskRpc, type ContainerResponse, type SpaceResponse, type TaskResponse } from "#neoastra";

const dom = new JSDOM("<!doctype html><html><body></body></html>", { url: "http://localhost" });
Object.defineProperties(globalThis, {
  window: { value: dom.window, configurable: true },
  document: { value: dom.window.document, configurable: true },
  navigator: { value: dom.window.navigator, configurable: true },
  HTMLElement: { value: dom.window.HTMLElement, configurable: true },
  crypto: { value: webcrypto, configurable: true },
  getComputedStyle: { value: dom.window.getComputedStyle.bind(dom.window), configurable: true },
  requestAnimationFrame: { value: (callback: FrameRequestCallback) => setTimeout(() => callback(Date.now()), 0), configurable: true },
  cancelAnimationFrame: { value: (handle: number) => clearTimeout(handle), configurable: true },
});
const { render, screen, waitFor, cleanup, within } = await import("@testing-library/react");
const userEvent = (await import("@testing-library/user-event")).default;
const { App } = await import("./App");
const { AssignmentPicker } = await import("./TaskEditor");
const { AssigneeChips, useAssigneeChips } = await import("./assignees");
const { accessIn, DeleteSpaceDialog, InviteDialog, NewSpaceDialog, SpaceHome } = await import("./Sharing");

const shared = "11111111-1111-4111-8111-111111111111";
const personal = "00000000-0000-4000-8000-000000000000";
const space = (role: number, name = "Bins"): SpaceResponse => ({ spaceId: shared, name, kind: "Shared", role, memberCount: 2 });
const personalSpace: SpaceResponse = { spaceId: personal, name: "Personal", kind: "Personal", role: null, memberCount: 1 };

function list(id: string, name: string): ContainerResponse {
  return { id, name, kind: "List", version: "1", archived: false, deadlineDate: null, defaultCategory: null };
}
function task(id: string, title: string, containerId: string, spaceId: string): TaskResponse {
  return {
    id,
    title,
    spaceId,
    containerId,
    notes: "",
    status: "NotStarted",
    plannedDate: null,
    dueDate: null,
    version: "1",
    createdAtUtc: "2026-01-01T00:00:00Z",
    updatedAtUtc: "2026-01-01T00:00:00Z",
    priority: "Normal",
    estimateMinutes: null,
    category: "Personal",
    plannedTime: null,
    plannedZoneId: null,
    dueTime: null,
    dueZoneId: null,
    sectionId: null,
  };
}

test("assignment picker offers owners and editors and shows completion", async () => {
  const requests: unknown[] = [];
  const item = task("t1", "Read", "bins", shared);
  const person = (id: string, role: number) => ({
    actorId: `person:${id}`,
    displayName: id,
    initials: id[0],
    colorIndex: 0,
    kind: "person",
    isMe: false,
    role,
    former: false,
  });
  const directoryApi = {
    list: async () => ({ ok: true, people: [person("Owner", 1), person("Editor", 2), person("Viewer", 3)], error: null }),
  };
  const api = {
    ...taskRpc,
    assignments: async () => ({
      ok: true,
      mode: "Each",
      assignees: [{ memberId: "Owner", completedAtUtc: "2026-11-21T00:00:00Z" }],
      error: null,
    }),
    changeAssignee: async (request: unknown) => {
      requests.push(request);
      return { ok: true, operationId: "op", task: item, error: null, warning: null };
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <AssignmentPicker
      task={item}
      taskApi={api}
      directoryApi={directoryApi}
      canEdit
      run={async (action, after) => {
        const result = await action();
        if (result.task) after?.(result.task);
      }}
      onSaved={() => {}}
      setError={() => {}}
    />,
    { container: dom.window.document.body },
  );
  try {
    assert.ok(await screen.findByText("Owner"));
    assert.ok(screen.getByText("Completed"));
    assert.equal(screen.queryByText("Viewer"), null);
    assert.equal((screen.getByRole("checkbox", { name: /Each assignee/ }) as HTMLInputElement).checked, true);
    await user.click(screen.getByRole("checkbox", { name: /Editor/ }));
    assert.equal((requests[0] as { memberId: string; assigned: boolean }).memberId, "Editor");
    assert.equal((requests[0] as { assigned: boolean }).assigned, true);
  } finally {
    cleanup();
  }
});

test("assignee chips read task assignments in one batch and show names and completion", async () => {
  const items = [task("t1", "One", "bins", shared), task("t2", "Two", "bins", shared)];
  const calls: string[][] = [];
  const api = {
    assignmentSummaries: async ({ taskIds }: { taskIds: readonly string[] }) => {
      calls.push([...taskIds]);
      return {
        ok: true,
        error: null,
        tasks: [{ taskId: "t1", spaceId: shared, assignees: [{ memberId: "a", completedAtUtc: "2026-11-21T00:00:00Z" }] }],
      };
    },
  };
  const directory = {
    list: async () => ({
      ok: true,
      error: null,
      people: [
        { actorId: "person:a", displayName: "Ana", initials: "A", colorIndex: 0, kind: "person", isMe: false, role: 2, former: false },
      ],
    }),
  };
  function Example() {
    const { chips } = useAssigneeChips(items, 0, api, directory);
    return <AssigneeChips people={chips.t1} />;
  }
  render(<Example />, { container: dom.window.document.body });
  try {
    assert.ok(await screen.findByText("Ana"));
    assert.ok(screen.getByLabelText("completed"));
    assert.deepEqual(calls, [["t1", "t2"]]);
  } finally {
    cleanup();
  }
});

test("access follows the role: Personal edits, owner and editor edit, viewer and unknown spaces do not", () => {
  const spaces = [personalSpace, space(1), { ...space(2), spaceId: "e" }, { ...space(3), spaceId: "v" }];
  assert.equal(accessIn(spaces, null).canEdit, true);
  assert.equal(accessIn(spaces, personal).shared, false);
  assert.equal(accessIn(spaces, shared).isOwner, true);
  assert.equal(accessIn(spaces, "e").canEdit, true);
  assert.equal(accessIn(spaces, "e").isOwner, false);
  const viewer = accessIn(spaces, "v");
  assert.deepEqual([viewer.canEdit, viewer.isViewer, viewer.sentence], [false, true, "You are a viewer"]);
  assert.equal(accessIn(spaces, "somewhere-else").canEdit, false);
  assert.equal(accessIn([], "anything").canEdit, true, "before the spaces load nothing is locked");
});

function appFor(role: number, tasks: TaskResponse[], calls: { moved?: unknown[] } = {}) {
  const sharedList = list("bins", "Bin days");
  const mine = list("home", "Home");
  return {
    taskRpc: {
      ...taskRpc,
      listInbox: async () => ({ ok: true, tasks: [], error: null }),
      listDeleted: async () => ({ ok: true, tasks: [], error: null }),
    },
    containerRpc: {
      ...containerRpc,
      list: async () => ({ ok: true, containers: [mine], error: null }),
      listDeleted: async () => ({ ok: true, containers: [], error: null }),
      listForSpace: async (request: { spaceId: string }) => ({
        ok: true,
        containers: request.spaceId === shared ? [sharedList] : [],
        error: null,
      }),
      tasks: async (request: { containerId: string }) => ({
        ok: true,
        tasks: tasks.filter((t) => t.containerId === request.containerId),
        error: null,
      }),
      sections: async () => ({ ok: true, sections: [], error: null }),
      deletedSections: async () => ({ ok: true, sections: [], error: null }),
    },
    spaceRpc: {
      ...spaceRpc,
      list: async () => ({ ok: true, spaces: [personalSpace, space(role)], error: null }),
      move: async (request: unknown) => {
        calls.moved?.push(request);
        return { ok: true, spaceId: shared, entityCount: 3, error: null };
      },
    },
  };
}

test("a viewer sees a shared list with no working edit control, and the task opens read-only", async () => {
  const items = [task("t1", "Put the green bin out", "bins", shared)];
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" api={appFor(3, items)} templates={<></>} />, { container: dom.window.document.body });
  try {
    await user.click(await screen.findByRole("button", { name: "Bin days" }));
    await screen.findByRole("button", { name: "Put the green bin out" });
    assert.ok(screen.getByText(/You are a viewer of this space/));
    assert.equal(screen.queryByRole("textbox", { name: "Add a task" }), null, "no quick add");
    assert.equal(screen.queryByRole("button", { name: "Add section" }), null, "no section creation");
    assert.equal((screen.getByRole("checkbox", { name: "Mark Put the green bin out as done" }) as HTMLInputElement).disabled, true);
    assert.equal((screen.getByRole("button", { name: "Delete Put the green bin out" }) as HTMLButtonElement).disabled, true);
    assert.equal((dom.window.document.querySelector('[aria-controls="list-settings"]') as HTMLButtonElement).disabled, true);
    assert.equal((screen.getByRole("button", { name: "Archive" }) as HTMLButtonElement).disabled, true);
    assert.equal(screen.queryByRole("button", { name: /Move to space/ }), null, "a shared list cannot be moved again");
    await user.click(screen.getByRole("button", { name: "Put the green bin out" }));
    const panel = await screen.findByRole("complementary", { name: "View task" });
    assert.equal(within(panel).queryByRole("button", { name: "Edit task" }), null);
    assert.ok(within(panel).getByText(/read-only/));
  } finally {
    cleanup();
  }
});

test("an editor can add and change tasks in a shared list", async () => {
  const items = [task("t1", "Put the green bin out", "bins", shared)];
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" api={appFor(2, items)} templates={<></>} />, { container: dom.window.document.body });
  try {
    await user.click(await screen.findByRole("button", { name: "Bin days" }));
    await screen.findByRole("button", { name: "Put the green bin out" });
    assert.ok(screen.getByRole("textbox", { name: "Add a task" }));
    assert.equal((screen.getByRole("checkbox", { name: "Mark Put the green bin out as done" }) as HTMLInputElement).disabled, false);
    assert.equal((dom.window.document.querySelector('[aria-controls="list-settings"]') as HTMLButtonElement).disabled, false);
  } finally {
    cleanup();
  }
});

test("Move to space… on a Personal list states what moves and calls the move", async () => {
  const calls: { moved: unknown[] } = { moved: [] };
  const user = userEvent.setup({ document: dom.window.document });
  render(<App initialView="tasks" api={appFor(1, [], calls)} templates={<></>} />, { container: dom.window.document.body });
  try {
    await user.click(await screen.findByRole("button", { name: "Home" }));
    assert.ok(screen.getByRole("button", { name: "Share list…" }));
    await user.click(screen.getByRole("button", { name: "Move to space…" }));
    const dialog = await screen.findByRole("dialog", { name: /Move “Home” to a space/ });
    assert.ok(within(dialog).getByText(/Its members will see them/));
    assert.ok(within(dialog).getByText(/My Day, reminders and personal tags stay yours/));
    await user.click(within(dialog).getByRole("button", { name: "Move" }));
    await waitFor(() => assert.deepEqual(calls.moved, [{ containerId: "home", spaceId: shared }]));
  } finally {
    cleanup();
  }
});

test("creating a space offline says to connect", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  let created = false;
  render(
    <NewSpaceDialog
      onClose={() => {}}
      onCreated={() => (created = true)}
      api={{
        create: async () => ({
          ok: false,
          space: null,
          error: { kind: "OperationConflict", message: "Connect to create a shared space.", fields: [], correlationId: "x" },
        }),
      }}
    />,
    { container: dom.window.document.body },
  );
  try {
    await user.type(screen.getByRole("textbox", { name: "Space name" }), "Neighbours");
    await user.click(screen.getByRole("button", { name: "Create space" }));
    assert.ok(await screen.findByText("Connect to create a shared space."));
    assert.equal(created, false);
  } finally {
    cleanup();
  }
});

test("a link invite shows its link once with Copy, and open invitations can be turned off", async () => {
  const requests: unknown[] = [];
  const revoked: string[] = [];
  let open = [
    {
      id: "i1",
      email: null as string | null,
      role: 2,
      createdAt: "2026-01-01T00:00:00Z",
      expiresAt: null as string | null,
      maxUses: 10 as number | null,
      uses: 0,
      revoked: false,
    },
  ];
  const api = {
    invites: async () => ({ ok: true, invites: open, error: null }),
    invite: async (request: unknown) => {
      requests.push(request);
      return { ok: true, invite: open[0], link: "https://chaos.example/invite/secret", error: null };
    },
    revokeInvite: async (request: { inviteId: string }) => {
      revoked.push(request.inviteId);
      open = [];
      return { ok: true, error: null };
    },
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<InviteDialog space={{ spaceId: shared, name: "Bins" }} api={api} onClose={() => {}} />, { container: dom.window.document.body });
  try {
    await user.click(screen.getByRole("button", { name: "By link" }));
    await user.selectOptions(screen.getByLabelText("Role"), "3");
    await user.selectOptions(screen.getByLabelText("Link expires"), "30");
    await user.selectOptions(screen.getByLabelText("Link can be used"), "10");
    await user.click(screen.getByRole("button", { name: "Create link" }));
    assert.ok(await screen.findByText("https://chaos.example/invite/secret"));
    assert.ok(screen.getByText(/shown only once/));
    assert.ok(screen.getByRole("button", { name: "Copy link" }));
    assert.deepEqual(requests, [{ spaceId: shared, email: null, role: 3, expiresInDays: 30, maxUses: 10 }]);
    await user.click(await screen.findByRole("button", { name: /Turn off invitation/ }));
    await waitFor(() => assert.deepEqual(revoked, ["i1"]));
    assert.ok(await screen.findByText("None."));
  } finally {
    cleanup();
  }
});

test("an email invite sends the address and role", async () => {
  const requests: unknown[] = [];
  const api = {
    invites: async () => ({ ok: true, invites: [], error: null }),
    invite: async (request: unknown) => {
      requests.push(request);
      return { ok: true, invite: null, link: null, error: null };
    },
    revokeInvite: async () => ({ ok: true, error: null }),
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<InviteDialog space={{ spaceId: shared, name: "Bins" }} api={api} onClose={() => {}} />, { container: dom.window.document.body });
  try {
    await user.type(screen.getByLabelText("Email address"), "sam@example.com");
    await user.click(screen.getByRole("button", { name: "Send invitation" }));
    assert.ok(await screen.findByText("Invitation sent to sam@example.com."));
    assert.deepEqual(requests, [{ spaceId: shared, email: "sam@example.com", role: 2, expiresInDays: null, maxUses: null }]);
  } finally {
    cleanup();
  }
});

test("deleting a space needs its name typed and explains the 30-day restore", async () => {
  const deleted: unknown[] = [];
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <DeleteSpaceDialog
      space={{ spaceId: shared, name: "Bins" }}
      onClose={() => {}}
      onDeleted={() => {}}
      api={{
        delete: async (request: unknown) => {
          deleted.push(request);
          return { ok: true, error: null };
        },
      }}
    />,
    { container: dom.window.document.body },
  );
  try {
    assert.ok(screen.getByText(/restore it within 30 days/));
    const button = screen.getByRole("button", { name: "Delete space" }) as HTMLButtonElement;
    assert.equal(button.disabled, true);
    await user.type(screen.getByLabelText(/Type “Bins” to confirm/), "Bins");
    assert.equal(button.disabled, false);
    await user.click(button);
    await waitFor(() => assert.deepEqual(deleted, [{ spaceId: shared, typedName: "Bins" }]));
  } finally {
    cleanup();
  }
});

function homeApi(role: number) {
  return {
    ...spaceRpc,
    members: async () => ({
      ok: true,
      members: [
        { memberId: "a", name: "Ana", role: 1, kind: 1, joinedAt: "2026-01-01T00:00:00Z", isMe: role === 1 },
        { memberId: "b", name: "Bo", role: role, kind: 1, joinedAt: "2026-01-02T00:00:00Z", isMe: role !== 1 },
      ],
      error: null,
    }),
  };
}

test("the owner manages roles and removal; a viewer only reads the member list and has no Settings", async () => {
  const user = userEvent.setup({ document: dom.window.document });
  const noop = () => {};
  render(<SpaceHome space={space(1)} lists={[]} onOpenList={noop} onLeft={noop} onChanged={noop} api={homeApi(1)} />, {
    container: dom.window.document.body,
  });
  try {
    assert.ok(screen.getByText(/You are an owner/));
    assert.ok(screen.getByRole("button", { name: "Invite people" }));
    assert.ok(screen.getByRole("tab", { name: "Settings" }));
    await user.click(screen.getByRole("tab", { name: "Members" }));
    assert.ok(await screen.findByRole("combobox", { name: "Role of Bo" }));
    assert.ok(screen.getByRole("button", { name: "Remove Bo" }));
    assert.equal(screen.queryByRole("button", { name: "Remove Ana" }), null, "no removing yourself; use Leave");
    assert.ok(screen.getByRole("textbox", { name: "My name in this space" }));
    cleanup();

    render(<SpaceHome space={space(3)} lists={[]} onOpenList={noop} onLeft={noop} onChanged={noop} api={homeApi(3)} />, {
      container: dom.window.document.body,
    });
    assert.ok(screen.getByText(/You are a viewer/));
    assert.equal(screen.queryByRole("button", { name: "Invite people" }), null);
    assert.equal(screen.queryByRole("tab", { name: "Settings" }), null);
    await user.click(screen.getByRole("tab", { name: "Members" }));
    await screen.findByText("Ana");
    assert.equal(screen.queryByRole("combobox", { name: /Role of/ }), null);
    assert.equal(screen.queryByRole("button", { name: /Remove/ }), null);
    assert.ok(screen.getByRole("button", { name: "Leave this space" }));
  } finally {
    cleanup();
  }
});

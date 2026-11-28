import { test } from "node:test";
import assert from "node:assert/strict";
import { webcrypto } from "node:crypto";
import { JSDOM } from "jsdom";
import React from "react";
import {
  agentRpc,
  containerRpc,
  gitHubRpc,
  spaceRpc,
  spaceUpdateRpc,
  taskRpc,
  type ContainerResponse,
  type SpaceResponse,
  type TaskResponse,
} from "#neoastra";

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
    claimedById: null,
    claimedAtUtc: null,
    claimExpiresAtUtc: null,
    agentReady: false,
    agentAssigneeId: null,
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
        { memberId: "a", name: "Ana", role: 1, kind: 1, joinedAt: "2026-01-01T00:00:00Z", isMe: role === 1, gitHubLogin: null },
        { memberId: "b", name: "Bo", role: role, kind: 1, joinedAt: "2026-01-02T00:00:00Z", isMe: role !== 1, gitHubLogin: null },
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
    assert.ok(screen.getByRole("tab", { name: "Connections" }));
    await user.click(screen.getByRole("tab", { name: "Members" }));
    assert.ok(await screen.findByRole("combobox", { name: "Role of Bo" }));
    assert.ok(screen.getByRole("button", { name: "Remove Bo" }));
    assert.equal(screen.queryByRole("button", { name: "Remove Ana" }), null, "no removing yourself; use Leave");
    assert.ok(screen.getByRole("textbox", { name: "My name in this space" }));
    assert.ok(screen.getByRole("textbox", { name: "My GitHub login" }));
    cleanup();

    render(<SpaceHome space={space(3)} lists={[]} onOpenList={noop} onLeft={noop} onChanged={noop} api={homeApi(3)} />, {
      container: dom.window.document.body,
    });
    assert.ok(screen.getByText(/You are a viewer/));
    assert.equal(screen.queryByRole("button", { name: "Invite people" }), null);
    assert.equal(screen.queryByRole("tab", { name: "Settings" }), null);
    assert.equal(screen.queryByRole("tab", { name: "Connections" }), null);
    await user.click(screen.getByRole("tab", { name: "Members" }));
    await screen.findByText("Ana");
    assert.equal(screen.queryByRole("combobox", { name: /Role of/ }), null);
    assert.equal(screen.queryByRole("button", { name: /Remove/ }), null);
    assert.ok(screen.getByRole("button", { name: "Leave this space" }));
  } finally {
    cleanup();
  }
});

test("the Members tab saves and clears a GitHub login, and the owner's Connections tab shows the space's GitHub settings", async () => {
  const logins: unknown[] = [];
  const api = {
    ...homeApi(1),
    setMyGitHubLogin: async (request: { spaceId: string; gitHubLogin: string | null }) => {
      logins.push(request);
      return { ok: true, error: null };
    },
  };
  const connectionsApi = {
    ...gitHubRpc,
    status: async () => ({
      ok: true,
      connection: null,
      repositories: [],
      targets: [],
      error: null,
      sync: null,
      isOwner: true,
      spaceId: shared,
      spaceName: "Bins",
      memberCount: 2,
      syncIntervalMinutes: 5,
    }),
  };
  const noop = () => {};
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <SpaceHome space={space(1)} lists={[]} onOpenList={noop} onLeft={noop} onChanged={noop} api={api} connectionsApi={connectionsApi} />,
    {
      container: dom.window.document.body,
    },
  );
  try {
    await user.click(screen.getByRole("tab", { name: "Members" }));
    const login = await screen.findByRole("textbox", { name: "My GitHub login" });
    assert.ok(screen.getByText(/Used to match GitHub assignees to you/));
    await user.type(login, "omar");
    await user.click(screen.getByRole("button", { name: "Save GitHub login" }));
    await screen.findByText("Your GitHub login was saved.");
    assert.deepEqual(logins, [{ spaceId: shared, gitHubLogin: "omar" }]);
    await user.clear(login);
    await user.click(screen.getByRole("button", { name: "Clear GitHub login" }));
    await screen.findByText("Your GitHub login was cleared.");
    assert.deepEqual(logins[1], { spaceId: shared, gitHubLogin: null });

    await user.click(screen.getByRole("tab", { name: "Connections" }));
    const panel = await screen.findByRole("tabpanel", { name: "Connections" });
    assert.ok(await within(panel).findByText(/Connect the issues of a GitHub repository to Bins/));
  } finally {
    cleanup();
  }
});

test("the Members tab lists agents without a role, lets an owner pause or remove one, and adds an agent with its lists", async () => {
  const added: unknown[] = [];
  const paused: unknown[] = [];
  const removed: unknown[] = [];
  const buildBot = { id: "bot", name: "Build bot", pausedAtUtc: null, memberships: [{ spaceId: shared, allowedContainerIds: null }] };
  const deployBot = { id: "deploy", name: "Deploy bot", pausedAtUtc: null, memberships: [] };
  const agents = [
    { agent: buildBot, ownerName: "Ana", member: true },
    { agent: deployBot, ownerName: "Bo", member: false },
  ];
  const api = {
    ...homeApi(1),
    members: async () => ({
      ok: true,
      error: null,
      members: [
        { memberId: "a", name: "Ana", role: 1, kind: 1, joinedAt: "2026-01-01T00:00:00Z", isMe: true, gitHubLogin: null },
        { memberId: "bot", name: "Build bot", role: 4, kind: 2, joinedAt: "2026-01-03T00:00:00Z", isMe: false, gitHubLogin: null },
      ],
    }),
  };
  const agentsApi = {
    ...agentRpc,
    spaceAgents: async () => ({ ok: true, agents, error: null }),
    addToSpace: async (request: unknown) => {
      added.push(request);
      return { ok: true, agents, error: null };
    },
    pauseInSpace: async (request: unknown) => {
      paused.push(request);
      return { ok: true, agent: agents[0], error: null };
    },
    removeFromSpace: async (request: unknown) => {
      removed.push(request);
      return { ok: true, agents: [], error: null };
    },
  };
  const noop = () => {};
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <SpaceHome
      space={space(1)}
      lists={[list("bins", "Bin days"), list("road", "Roadmap")]}
      onOpenList={noop}
      onLeft={noop}
      onChanged={noop}
      api={api}
      agentsApi={agentsApi}
    />,
    { container: dom.window.document.body },
  );
  try {
    await user.click(screen.getByRole("tab", { name: "Members" }));
    const people = await screen.findByRole("list", { name: "People in this space" });
    await within(people).findByText("(agent)");
    assert.ok(within(people).getByText("Build bot"));
    assert.equal(within(people).queryByRole("combobox", { name: "Role of Build bot" }), null, "an agent has no owner or editor role");
    await user.click(within(people).getByRole("button", { name: "Pause Build bot" }));
    await waitFor(() => assert.deepEqual(paused, [{ spaceId: shared, agentId: "bot", paused: true }]));
    await screen.findByText("Build bot was paused.");

    await user.click(screen.getByRole("button", { name: "Add agent" }));
    const dialog = await screen.findByRole("dialog", { name: "Add an agent to Bins" });
    const select = within(dialog).getByLabelText("Agent") as HTMLSelectElement;
    assert.equal(select.value, "deploy", "an agent already in the space is not offered");
    assert.match(select.textContent ?? "", /Deploy bot \(Bo\)/);
    assert.equal((within(dialog).getByRole("checkbox", { name: "All lists" }) as HTMLInputElement).checked, true);
    await user.click(within(dialog).getByRole("checkbox", { name: "All lists" }));
    await user.click(within(dialog).getByRole("checkbox", { name: "Bin days" }));
    await user.click(within(dialog).getByRole("button", { name: "Add agent" }));
    await waitFor(() => assert.deepEqual(added, [{ spaceId: shared, agentId: "deploy", allowedContainerIds: ["bins"] }]));
    await screen.findByText("Agent added.");

    await user.click(within(people).getByRole("button", { name: "Remove Build bot" }));
    await waitFor(() => assert.deepEqual(removed, [{ spaceId: shared, agentId: "bot" }]));
    await screen.findByText("Build bot was removed.");
  } finally {
    cleanup();
  }

  // A viewer sees the agent named, with nothing to change and no way to add one.
  render(<SpaceHome space={space(3)} lists={[]} onOpenList={noop} onLeft={noop} onChanged={noop} api={api} agentsApi={agentsApi} />, {
    container: dom.window.document.body,
  });
  try {
    await user.click(screen.getByRole("tab", { name: "Members" }));
    const readOnly = await screen.findByRole("list", { name: "People in this space" });
    await within(readOnly).findByText("(agent)");
    assert.equal(screen.queryByRole("button", { name: "Add agent" }), null);
    assert.equal(within(readOnly).queryByRole("button", { name: /Build bot/ }), null);
  } finally {
    cleanup();
  }
});

test("the invite dialog warns about a private repository connected to the space and needs an acknowledgement", async () => {
  const requests: unknown[] = [];
  const api = {
    invites: async () => ({ ok: true, invites: [], error: null }),
    invite: async (request: unknown) => {
      requests.push(request);
      return { ok: true, invite: null, link: "https://chaos.example/invite/private", error: null };
    },
    revokeInvite: async () => ({ ok: true, error: null }),
  };
  const githubApi = {
    ...gitHubRpc,
    status: async () => ({
      ok: true,
      connection: {
        id: "c1",
        version: "1",
        accountId: "3000000000",
        accountLogin: "person",
        state: "Active",
        postDiaryByDefault: true,
        createLabelsForTags: false,
        runnerDeviceName: "Desk",
        isRunner: true,
      },
      repositories: [
        {
          id: "r1",
          version: "1",
          repositoryId: "4000000000",
          fullName: "acme/secret",
          state: "Active",
          targetContainerId: "inbox",
          targetSectionId: null,
          filter: { labels: [], labelMode: "any", assignee: "any", milestone: null, noMilestone: false, includeClosedDays: 0 },
          milestonesAsProjects: false,
          taskCategory: "Personal",
          isPrivate: true,
        },
        {
          id: "r2",
          version: "1",
          repositoryId: "4000000001",
          fullName: "acme/open",
          state: "Active",
          targetContainerId: "inbox",
          targetSectionId: null,
          filter: { labels: [], labelMode: "any", assignee: "any", milestone: null, noMilestone: false, includeClosedDays: 0 },
          milestonesAsProjects: false,
          taskCategory: "Personal",
          isPrivate: false,
        },
      ],
      targets: [],
      error: null,
      sync: null,
      isOwner: true,
      spaceId: shared,
      spaceName: "Bins",
      memberCount: 1,
      syncIntervalMinutes: 5,
    }),
  };
  const user = userEvent.setup({ document: dom.window.document });
  render(<InviteDialog space={{ spaceId: shared, name: "Bins" }} api={api} githubApi={githubApi} onClose={() => {}} />, {
    container: dom.window.document.body,
  });
  try {
    const warning = await screen.findByRole("alert");
    assert.match(
      warning.textContent ?? "",
      /Everyone in Bins will see issues from acme\/secret, including people without access to it on GitHub\./,
    );
    await user.click(screen.getByRole("button", { name: "By link" }));
    const create = screen.getByRole("button", { name: "Create link" });
    assert.equal((create as HTMLButtonElement).disabled, true);
    await user.click(screen.getByRole("checkbox", { name: "I understand; invite anyway" }));
    assert.equal((create as HTMLButtonElement).disabled, false);
    await user.click(create);
    assert.ok(await screen.findByText("https://chaos.example/invite/private"));
    assert.equal(requests.length, 1);
  } finally {
    cleanup();
  }
});

test("the Overview lists standups newest first; the author edits, the author or an owner deletes, and a viewer cannot touch others'", async () => {
  const calls: string[] = [];
  const standup = (id: string, author: string, isMine: boolean, created: string) => ({
    id,
    spaceId: shared,
    authorMemberId: isMine ? "a" : "b",
    forDate: "2026-11-20",
    rawMarkdown: "## Done\n- " + id,
    editedAtUtc: null,
    createdAtUtc: created,
    version: "1",
    authorName: author,
    isMine,
  });
  const updates = [standup("s2", "Bo", true, "2026-11-20T18:00:00Z"), standup("s1", "Ana Lopez", false, "2026-11-20T17:00:00Z")];
  const updatesApi = {
    ...spaceUpdateRpc,
    list: async () => ({ ok: true, updates, error: null }),
    edit: async (request: { updateId: string; expectedVersion: string; markdown: string }) => {
      calls.push(`edit ${request.updateId} ${request.expectedVersion} ${request.markdown}`);
      return { ok: true, update: null, replayed: false, error: null };
    },
    delete: async (request: { updateId: string; expectedVersion: string }) => {
      calls.push(`delete ${request.updateId} ${request.expectedVersion}`);
      return { ok: true, update: null, replayed: false, error: null };
    },
  } as unknown as typeof spaceUpdateRpc;
  const noop = () => {};
  const user = userEvent.setup({ document: dom.window.document });
  render(
    <SpaceHome
      space={space(1)}
      lists={[]}
      onOpenList={noop}
      onLeft={noop}
      onChanged={noop}
      api={homeApi(1)}
      updatesApi={updatesApi}
      revision={0}
    />,
    { container: dom.window.document.body },
  );
  try {
    const region = await screen.findByRole("region", { name: "Standups" });
    const items = within(region).getAllByRole("listitem", { name: /Standup by/ });
    assert.equal(items.length, 2);
    assert.ok(within(items[0]).getByText("Bo"), "newest first");
    assert.ok(within(items[1]).getByText("Ana Lopez"));
    assert.ok(within(items[0]).getByRole("heading", { name: "Done" }), "the standup renders as Markdown");
    assert.equal(within(items[1]).queryByRole("button", { name: "Edit" }), null, "only the author edits");
    assert.ok(within(items[0]).getByRole("button", { name: "Edit" }), "the author edits");
    assert.ok(within(items[1]).getByRole("button", { name: "Delete standup by Ana Lopez" }), "an owner may delete another's standup");

    await user.click(within(items[1]).getByRole("button", { name: "Delete standup by Ana Lopez" }));
    assert.deepEqual(calls, [], "the first click only asks");
    await user.click(within(items[1]).getByRole("button", { name: "Delete" }));
    await waitFor(() => assert.deepEqual(calls, ["delete s1 1"]));
    await screen.findByText("Standup deleted.");

    await user.click(within(items[0]).getByRole("button", { name: "Edit" }));
    const editor = within(items[0]).getByRole("textbox", { name: "Standup" }) as HTMLTextAreaElement;
    await user.clear(editor);
    await user.type(editor, "## Done\n- s2\n- edited");
    await user.click(within(items[0]).getByRole("button", { name: "Save standup" }));
    await waitFor(() => assert.equal(calls[1], "edit s2 1 ## Done\n- s2\n- edited"));
  } finally {
    cleanup();
  }

  // A viewer edits and deletes only their own standup.
  render(
    <SpaceHome
      space={space(3)}
      lists={[]}
      onOpenList={noop}
      onLeft={noop}
      onChanged={noop}
      api={homeApi(3)}
      updatesApi={updatesApi}
      revision={0}
    />,
    { container: dom.window.document.body },
  );
  try {
    const region = await screen.findByRole("region", { name: "Standups" });
    const items = within(region).getAllByRole("listitem", { name: /Standup by/ });
    assert.ok(within(items[0]).getByRole("button", { name: "Edit" }));
    assert.equal(within(items[1]).queryByRole("button", { name: /Delete/ }), null);
    assert.equal(within(items[1]).queryByRole("button", { name: "Edit" }), null);
  } finally {
    cleanup();
  }
});

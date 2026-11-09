import React from "react";
import { createRoot } from "react-dom/client";
import "./style.css";
import { taskRpc, type TaskResponse } from "#neoastra";

function App() {
  const [title, setTitle] = React.useState("");
  const [notes, setNotes] = React.useState("");
  const [tasks, setTasks] = React.useState<TaskResponse[]>([]);
  const [error, setError] = React.useState<string | null>(null);
  const [busy, setBusy] = React.useState(false);

  async function capture(event: React.FormEvent) {
    event.preventDefault();
    if (busy) return;
    setBusy(true);
    setError(null);
    try {
      const response = await taskRpc.create({
        operationId: crypto.randomUUID(),
        title,
        notes: notes.length > 0 ? notes : null,
        containerId: null,
      });
      const task = response.task;
      if (response.ok && task) {
        setTasks((current) => [task, ...current]);
        setTitle("");
        setNotes("");
      } else if (response.error) {
        const fields = response.error.fields
          .map((field) => `${field.fieldPath}: ${field.message}`)
          .join("; ");
        setError(fields.length > 0 ? fields : response.error.message);
      } else {
        setError("The task could not be saved. Try again.");
      }
    } finally {
      setBusy(false);
    }
  }

  return (
    <>
      <h1>Chaos</h1>
      <form onSubmit={(event) => void capture(event)}>
        <label htmlFor="task-title">Task</label>
        <input
          id="task-title"
          value={title}
          onChange={(event) => setTitle(event.target.value)}
          placeholder="What needs doing?"
          autoComplete="off"
        />
        <label htmlFor="task-notes">Notes</label>
        <textarea
          id="task-notes"
          value={notes}
          onChange={(event) => setNotes(event.target.value)}
          rows={3}
        />
        <button type="submit" disabled={busy || title.trim().length === 0}>
          Add to Inbox
        </button>
      </form>
      {error && (
        <p role="alert">{error}</p>
      )}
      {tasks.length === 0 ? (
        <p>Nothing captured yet.</p>
      ) : (
        <ul aria-label="Inbox">
          {tasks.map((task) => (
            <li key={task.id}>
              <strong>{task.title}</strong>
              {task.notes.length > 0 && <span> — {task.notes}</span>}
              <small> [{task.status}]</small>
            </li>
          ))}
        </ul>
      )}
    </>
  );
}

createRoot(document.querySelector("#app")!).render(<App />);

import React, { useEffect, useState } from "react";
import { BookOpen, CalendarDays, CheckSquare, ChevronRight, Search, StickyNote } from "lucide-react";
import { searchContent } from "./mobileData.js";

const groups = [
  { kind: "task", label: "Tasks", Icon: CheckSquare },
  { kind: "pad", label: "Personal pad", Icon: StickyNote },
  { kind: "page", label: "Pages", Icon: BookOpen },
];

export function SearchScreen({ initialQuery, session, offline, search = searchContent }) {
  const [query, setQuery] = useState(initialQuery.slice(0, 200));
  const [result, setResult] = useState({ status: "idle", items: [] });
  const trimmed = query.trim();
  useEffect(() => {
    if (!trimmed || offline || !session) {
      setResult({ status: "idle", items: [] });
      return;
    }
    const controller = new AbortController();
    setResult({ status: "loading", items: [] });
    const timer = setTimeout(() => {
      search(session, trimmed, controller.signal).then(data => {
        if (!controller.signal.aborted) setResult({ status: "ready", ...data });
      }).catch(error => {
        if (!controller.signal.aborted) setResult({ status: "error", items: [], error: error.message });
      });
    }, 300);
    return () => { clearTimeout(timer); controller.abort(); };
  }, [trimmed, offline, session?.deviceId, search]);

  function change(event) {
    const value = event.target.value;
    setQuery(value);
    history.replaceState(history.state, "", value ? `#search?q=${encodeURIComponent(value)}` : "#search");
  }
  const from = query ? `#search?q=${encodeURIComponent(query)}` : "#search";
  const href = item => item.kind === "task" ? `#task/${item.id}?from=${encodeURIComponent(from)}`
    : `#${item.kind === "page" ? "pages" : "pad"}/${item.id}?from=${encodeURIComponent(from)}`;
  return <>
    <div className="screen-heading"><h1>Search</h1></div>
    <label className="search-label" htmlFor="search-query">Search tasks, notes, and pages</label>
    <div className="search-field"><Search size={19} aria-hidden="true" />
      <input id="search-query" type="search" maxLength={200} value={query} onChange={change} placeholder="Search Chaos" autoComplete="off" />
    </div>
    {offline ? <p className="search-status" role="status">Connect to search your synced content.</p>
      : !trimmed ? <p className="search-status">Search titles and content across your tasks, Personal pad, and pages.</p>
      : result.status === "loading" ? <p className="search-status" role="status">Searching…</p>
      : result.status === "error" ? <p className="search-status" role="alert">{result.error}</p>
      : result.status === "ready" && !result.items.length ? <p className="search-status" role="status">No results found.</p>
      : result.status === "ready" && <>
        <p className="search-status" role="status">{result.truncated ? "Showing the first 100 results. Refine your search for more." : `${result.items.length} ${result.items.length === 1 ? "result" : "results"}`}</p>
        {groups.map(({ kind, label, Icon }) => {
          const items = result.items.filter(item => item.kind === kind);
          return items.length > 0 && <section className="page-section search-results" key={kind} aria-labelledby={`search-${kind}`}>
            <h2 id={`search-${kind}`}>{label} <span>{items.length}</span></h2>
            <ul className="rows">{items.map(item => <li key={item.id}><a className="list-row" href={href(item)}>
              <span className="list-icon">{item.detail?.startsWith("Daily page") ? <CalendarDays /> : <Icon />}</span>
              <span className="row-title">{item.title}<small>{[item.detail, item.snippet].filter(Boolean).join(" · ")}</small></span>
              <ChevronRight className="row-arrow" size={18} />
            </a></li>)}</ul>
          </section>;
        })}
      </>}
  </>;
}

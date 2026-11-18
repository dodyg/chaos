import React, { useEffect, useRef, useState } from "react";
import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";

const remarkPlugins = [remarkGfm];
function MermaidDiagram({ source }) {
  const host = useRef(null);
  const [error, setError] = useState(null);
  useEffect(() => {
    const element = host.current;
    let disposed = false;
    let diagram;
    setError(null);
    import("pele").then(({ mount }) => {
      if (disposed) return;
      diagram = mount(element, source, {
        images: false,
        onRender: () => {
          for (const item of element.querySelectorAll("[style]")) {
            // CSP blocks style attributes and cssText, but allows individual CSSOM properties.
            const declarations = [...item.style].map(name => [name, item.style.getPropertyValue(name), item.style.getPropertyPriority(name)]);
            for (const [name, value, priority] of declarations) item.style.setProperty(name, value, priority);
          }
          setError(null);
        },
        onError: failure => setError(failure instanceof Error ? failure.message : "Could not draw this diagram."),
      });
    }).catch(failure => {
      if (!disposed) setError(failure instanceof Error ? failure.message : "Could not draw this diagram.");
    });
    return () => { disposed = true; diagram?.destroy(); };
  }, [source]);
  return <div className="mermaid-block">
    <div ref={host} className="mermaid-diagram" role="img" aria-label="Mermaid diagram" hidden={Boolean(error)} />
    {error && <><p role="status">Could not draw this diagram: {error}</p><pre><code>{source}</code></pre></>}
  </div>;
}

const components = {
  pre({ node, children, ...props }) {
    const code = node?.children?.length === 1 ? node.children[0] : null;
    if (code?.tagName === "code" && code.properties?.className?.some(name => name.toLowerCase() === "language-mermaid")) {
      return <MermaidDiagram source={code.children.map(child => child.value ?? "").join("")} />;
    }
    return <pre {...props}>{children}</pre>;
  },
  h1: "h3", h2: "h4", h3: "h5", h4: "h6", h5: "h6",
  a({ node, href, children, ...props }) {
    if (!href) return children;
    return <a {...props} href={href} onClick={event => {
      if (!href.startsWith("#")) return;
      const target = document.getElementById(href.slice(1));
      if (!event.currentTarget.closest(".markdown-notes")?.contains(target)) return;
      event.preventDefault();
      target.scrollIntoView({ block: "nearest" });
    }}>{children}</a>;
  },
  img({ alt }) {
    return <span className="note-image" role="img" aria-label={alt ? `Image unavailable: ${alt}` : "Image unavailable"}>
      {alt || "Image unavailable"}
    </span>;
  },
  input({ checked }) {
    return <input type="checkbox" checked={checked} disabled aria-label={checked ? "Done" : "Not done"} />;
  },
  table({ children }) {
    return <div className="note-table"><table>{children}</table></div>;
  },
};

export function MarkdownNotes({ children }) {
  return <div className="markdown-notes">
    <Markdown remarkPlugins={remarkPlugins} components={components}>{children}</Markdown>
  </div>;
}

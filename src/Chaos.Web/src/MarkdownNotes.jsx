import React, { useEffect, useRef, useState } from "react";
import Markdown, { defaultUrlTransform } from "react-markdown";
import remarkGfm from "remark-gfm";
import {
  diagramFailureMessage,
  isMemberMention,
  isSafeExternalUrl,
  reapplyInlineStyles,
} from "../../Chaos.Desktop/frontend/src/markdownCore";

const remarkPlugins = [remarkGfm];
function MermaidDiagram({ source }) {
  const host = useRef(null);
  const [error, setError] = useState(null);
  useEffect(() => {
    const element = host.current;
    let disposed = false;
    let diagram;
    setError(null);
    import("pele")
      .then(({ mount }) => {
        if (disposed) return;
        diagram = mount(element, source, {
          images: false,
          onRender: () => {
            reapplyInlineStyles(element);
            setError(null);
          },
          onError: (failure) => setError(diagramFailureMessage(failure)),
        });
      })
      .catch((failure) => {
        if (!disposed) setError(diagramFailureMessage(failure));
      });
    return () => {
      disposed = true;
      diagram?.destroy();
    };
  }, [source]);
  return (
    <div className="mermaid-block">
      <div ref={host} className="mermaid-diagram" role="img" aria-label="Mermaid diagram" hidden={Boolean(error)} />
      {error && (
        <>
          <p role="status">Could not draw this diagram: {error}</p>
          <pre>
            <code>{source}</code>
          </pre>
        </>
      )}
    </div>
  );
}

// A note holds attachments as chaos:attachment/<id>. The session cookie only travels under /v1/web, so images load from there.
const attachmentLink = /^chaos:attachment\/([0-9a-fA-F-]{36})$/;
export function attachmentUrl(src) {
  const match = typeof src === "string" ? attachmentLink.exec(src) : null;
  return match ? `/v1/web/attachments/${match[1].toLowerCase()}` : null;
}
function urlTransform(url) {
  return attachmentLink.test(url) || isMemberMention(url) ? url : defaultUrlTransform(url);
}

function NoteImage({ src, alt }) {
  const url = attachmentUrl(src);
  const [failed, setFailed] = useState(false);
  if (!url || failed) {
    return (
      <span className="note-image" role="img" aria-label={alt ? `Image unavailable: ${alt}` : "Image unavailable"}>
        {alt || "Image unavailable"}
      </span>
    );
  }
  return <img className="note-picture" src={url} alt={alt ?? ""} loading="lazy" onError={() => setFailed(true)} />;
}

const components = {
  pre({ node, children, ...props }) {
    const code = node?.children?.length === 1 ? node.children[0] : null;
    if (code?.tagName === "code" && code.properties?.className?.some((name) => name.toLowerCase() === "language-mermaid")) {
      return <MermaidDiagram source={code.children.map((child) => child.value ?? "").join("")} />;
    }
    return <pre {...props}>{children}</pre>;
  },
  h1: "h3",
  h2: "h4",
  h3: "h5",
  h4: "h6",
  h5: "h6",
  a({ node, href, children, ...props }) {
    if (href && isMemberMention(href))
      return (
        <span className="member-mention" data-mention="true">
          {children}
        </span>
      );
    // Only http, https and mailto links are clickable, as in the desktop app; same-note anchors still work.
    if (!href || (!href.startsWith("#") && !isSafeExternalUrl(href))) return children;
    return (
      <a
        {...props}
        href={href}
        onClick={(event) => {
          if (!href.startsWith("#")) return;
          const target = document.getElementById(href.slice(1));
          if (!event.currentTarget.closest(".markdown-notes")?.contains(target)) return;
          event.preventDefault();
          target.scrollIntoView({ block: "nearest" });
        }}
      >
        {children}
      </a>
    );
  },
  img({ src, alt }) {
    return <NoteImage src={src} alt={alt} />;
  },
  input({ checked }) {
    return <input type="checkbox" checked={checked} disabled aria-label={checked ? "Done" : "Not done"} />;
  },
  table({ children }) {
    return (
      <div className="note-table">
        <table>{children}</table>
      </div>
    );
  },
};

export function MarkdownNotes({ children }) {
  return (
    <div className="markdown-notes">
      <Markdown remarkPlugins={remarkPlugins} components={components} urlTransform={urlTransform}>
        {children}
      </Markdown>
    </div>
  );
}

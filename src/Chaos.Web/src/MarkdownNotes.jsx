import Markdown from "react-markdown";
import remarkGfm from "remark-gfm";

const remarkPlugins = [remarkGfm];
const components = {
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

import { frontMatterValue, splitPage } from "../../Chaos.Desktop/frontend/src/frontMatter";

export function pageContent(markdown) {
  const { body, frontMatter } = splitPage(markdown);
  if (frontMatter.error) return { body: markdown, background: null };
  return { body, background: frontMatterValue(frontMatter, "background") };
}

import { createGetUrl } from 'fumadocs-core/source';

export const appName = 'XIV MCP';
export const tagline = 'Uplink AI assistants to Final Fantasy XIV';
export const docsRoute = '/docs';
export const docsImageRoute = '/og/docs';
export const docsContentRoute = '/llms.mdx/docs';

/** The path the site is served under (see next.config.mjs). Links through next/link add it themselves; fetch() and <img> need it. */
export const basePath = process.env.NEXT_PUBLIC_BASE_PATH ?? '';
export const siteUrl = `https://individualgather.github.io${basePath}`;

/** Prefixes a site path with the base path, for fetches and plain <img> tags. */
export const withBase = (path: string) => `${basePath}${path}`;

export const gitConfig = {
  user: 'IndividualGather',
  repo: 'xiv-mcp',
  branch: 'master',
};

export const repoUrl = `https://github.com/${gitConfig.user}/${gitConfig.repo}`;

const getContentUrl = createGetUrl(docsContentRoute);

export function getPageMarkdownUrl(page: { slugs: string[]; locale?: string }) {
  const segments = [...page.slugs, 'content.md'];

  return { segments, url: getContentUrl(segments, page.locale) };
}

const getImageUrl = createGetUrl(docsImageRoute);

export function getPageImageUrl(page: { slugs: string[]; locale?: string }) {
  const segments = [...page.slugs, 'image.png'];

  return { segments, url: getImageUrl(segments, page.locale) };
}

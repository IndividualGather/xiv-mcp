import { createMDX } from 'fumadocs-mdx/next';

const withMDX = createMDX();

// GitHub Pages serves the site from /<repo>. Set DOCS_BASE_PATH="" to serve it from the root.
const basePath = process.env.DOCS_BASE_PATH ?? '/xiv-mcp';

/** @type {import('next').NextConfig} */
const config = {
  output: 'export',
  basePath,
  trailingSlash: true,
  images: { unoptimized: true },
  env: { NEXT_PUBLIC_BASE_PATH: basePath },
  reactStrictMode: true,
};

export default withMDX(config);

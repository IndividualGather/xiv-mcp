import { llms, loader } from 'fumadocs-core/source';
import { docsContentRoute, docsImageRoute, docsRoute } from './shared';
import { defineDocs } from 'fumadocs-mdx/macro';
import { metaSchema, pageSchema } from 'fumadocs-core/source/schema';
import { lucideIconsPlugin } from 'fumadocs-core/source/plugins/lucide-icons';
import { applyMdxPreset } from 'fumadocs-mdx/config';
import { xivMcpDark } from './code-theme';

const docs = defineDocs({
  dir: 'content/docs',
  docs: {
    schema: pageSchema,
    postprocess: {
      includeProcessedMarkdown: true,
    },
    // Code in the brand colours; `code{:lang}` highlights inline code too (used in the reference tables).
    mdxOptions: applyMdxPreset({
      rehypeCodeOptions: {
        themes: { light: 'github-light', dark: xivMcpDark },
        inline: 'tailing-curly-colon',
      },
    }),
  },
  meta: {
    schema: metaSchema,
  },
});

// See https://fumadocs.dev/docs/headless/source-api for more info
export const source = loader({
  baseUrl: docsRoute,
  source: docs.toFumadocsSource(),
  plugins: [lucideIconsPlugin()],
});

export const docsLlms = llms(source, {
  renderPage: async (page) => `# ${page.data.title} (${page.url})

${await page.data.getText('processed')}`,
});

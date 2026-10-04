'use client';

import { use, useId, useSyncExternalStore } from 'react';
import { useTheme } from 'next-themes';

/**
 * A Mermaid diagram, drawn in the browser (the site is a static export). Use it in MDX as
 * <Mermaid chart="flowchart LR; A --> B" />. Colours follow the XIV MCP brand in dark and light mode.
 */
export function Mermaid({ chart, caption }: { chart: string; caption?: string }) {
  // false on the server and during hydration: the diagram is only drawn in the browser
  const isClient = useSyncExternalStore(
    () => () => {},
    () => true,
    () => false,
  );

  return (
    <figure className="not-prose my-6 overflow-x-auto rounded-xl border border-fd-border bg-fd-card p-4">
      {isClient ? <MermaidContent chart={chart} /> : <div className="h-40" />}
      {caption && <figcaption className="mt-3 text-center text-sm text-fd-muted-foreground">{caption}</figcaption>}
    </figure>
  );
}

const cache = new Map<string, Promise<unknown>>();

function cachePromise<T>(key: string, create: () => Promise<T>): Promise<T> {
  const cached = cache.get(key);
  if (cached) return cached as Promise<T>;
  const promise = create();
  cache.set(key, promise);
  return promise;
}

const dark = {
  background: 'transparent',
  fontFamily: 'inherit',
  primaryColor: '#16302b',
  primaryTextColor: '#e0e0e0',
  primaryBorderColor: '#3befc4',
  secondaryColor: '#17223d',
  secondaryBorderColor: '#3f7bff',
  tertiaryColor: '#232326',
  lineColor: '#7d8bb0',
  textColor: '#e0e0e0',
  noteBkgColor: '#232326',
  noteTextColor: '#e0e0e0',
  noteBorderColor: '#3f7bff',
  actorBkg: '#16302b',
  actorBorder: '#3befc4',
  actorTextColor: '#e0e0e0',
  actorLineColor: '#4a5068',
  signalColor: '#c9d1e6',
  signalTextColor: '#e0e0e0',
  labelBoxBkgColor: '#17223d',
  labelBoxBorderColor: '#3f7bff',
  labelTextColor: '#e0e0e0',
  loopTextColor: '#e0e0e0',
  activationBkgColor: '#17223d',
  activationBorderColor: '#3f7bff',
  edgeLabelBackground: '#1a1a1a',
  clusterBkg: '#1f1f21',
  clusterBorder: '#3a3f4f',
};

const light = {
  background: 'transparent',
  fontFamily: 'inherit',
  primaryColor: '#dcfbf3',
  primaryTextColor: '#10201c',
  primaryBorderColor: '#1fae8c',
  secondaryColor: '#e2ebff',
  secondaryBorderColor: '#2457d6',
  tertiaryColor: '#f2f2f2',
  lineColor: '#5a6480',
  textColor: '#1a1a1a',
  noteBkgColor: '#f4f6fb',
  noteTextColor: '#1a1a1a',
  noteBorderColor: '#2457d6',
  actorBkg: '#dcfbf3',
  actorBorder: '#1fae8c',
  actorTextColor: '#10201c',
  signalColor: '#3a4258',
  signalTextColor: '#1a1a1a',
  labelBoxBkgColor: '#e2ebff',
  labelBoxBorderColor: '#2457d6',
  edgeLabelBackground: '#ffffff',
};

function MermaidContent({ chart }: { chart: string }) {
  const id = useId().replaceAll(':', '');
  const { resolvedTheme } = useTheme();
  const theme = resolvedTheme === 'light' ? 'light' : 'dark';
  const { default: mermaid } = use(cachePromise('mermaid', () => import('mermaid')));

  mermaid.initialize({
    startOnLoad: false,
    securityLevel: 'strict',
    theme: 'base',
    themeVariables: theme === 'dark' ? dark : light,
    sequence: { mirrorActors: false, useMaxWidth: true },
    flowchart: { useMaxWidth: true, curve: 'basis' },
  });

  const { svg } = use(cachePromise(`${chart}-${theme}`, () => mermaid.render(`mermaid-${id}-${theme}`, chart.replaceAll('\\n', '\n'))));

  return <div className="flex justify-center [&_svg]:h-auto [&_svg]:max-w-full" dangerouslySetInnerHTML={{ __html: svg }} />;
}

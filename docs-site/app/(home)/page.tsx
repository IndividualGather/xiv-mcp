import Link from 'next/link';
import {
  BookOpen,
  Boxes,
  Coins,
  Hammer,
  ListChecks,
  Map,
  Plug,
  ShieldCheck,
  Swords,
  UserRound,
} from 'lucide-react';
import { repoUrl, tagline, withBase } from '@/lib/shared';

const features = [
  {
    icon: UserRound,
    title: 'Your character, live',
    text: 'Jobs, gear, stats, inventory, currencies, quests, unlocks, party and surroundings, read straight from the game.',
    chips: ['reading', 'on by default'],
  },
  {
    icon: Boxes,
    title: 'Items and retainers',
    text: 'Find any item across bags, saddlebag and retainers, sort and move stacks, send retainers on ventures.',
    chips: ['inventory', 'ventures'],
  },
  {
    icon: Coins,
    title: 'Market and vendors',
    text: 'Check prices, find vendors, buy and sell, and undercut cheaper listings within limits you set.',
    chips: ['market board', 'approvals'],
  },
  {
    icon: Hammer,
    title: 'Crafting and gathering',
    text: 'Plan a project from the recipe tree, gather what is missing and craft the list in the right order.',
    chips: ['plans', 'lists'],
  },
  {
    icon: Swords,
    title: 'Dungeons',
    text: 'Run and loop duties until you have the drop or the tomestones you are after.',
    chips: ['loops', 'targets'],
  },
  {
    icon: Map,
    title: 'Maps and routes',
    text: 'Where you are, flags on the map in any zone, and routes that flag each stop once you reach the one before.',
    chips: ['flags', 'routes'],
  },
  {
    icon: ListChecks,
    title: 'Background jobs',
    text: 'Chains of steps that keep running for hours, independent of the chat, and survive reloads.',
    chips: ['queues', 'pause / resume'],
  },
  {
    icon: Plug,
    title: 'Plugins of your choice',
    text: 'Other plugins can offer their own tools. Nothing runs until you enable it.',
    chips: ['plugin API', 'consent'],
  },
];

export default function HomePage() {
  return (
    <main className="flex flex-1 flex-col items-center px-4 pb-20">
      <section className="flex max-w-3xl flex-col items-center pt-16 text-center sm:pt-24">
        <img src={withBase('/logo.svg')} alt="" width={112} height={112} className="mb-6 drop-shadow-[0_0_40px_rgba(59,239,196,0.25)]" />
        <h1 className="xiv-wordmark text-5xl font-bold tracking-tight sm:text-6xl">XIV MCP</h1>
        <p className="mt-4 text-lg text-fd-muted-foreground sm:text-xl">{tagline}</p>
        <p className="mt-6 max-w-2xl text-fd-muted-foreground">
          A Dalamud plugin that runs a Model Context Protocol server inside the game. Claude, ChatGPT, LM Studio and other AI apps
          can see your character and, when you allow it, act for you. Reading is allowed by default; acting, editing and going online are off until you
          switch them on. Every setting is yours to change.
        </p>

        <nav className="mt-8 flex flex-wrap justify-center gap-1 rounded-lg border border-fd-border p-1 text-sm">
          <Link href="/docs/user" className="flex items-center gap-2 rounded-md bg-fd-accent px-4 py-2 font-medium text-fd-accent-foreground">
            <BookOpen className="size-4 text-fd-primary" /> User docs
          </Link>
          <Link href="/docs/developer" className="flex items-center gap-2 rounded-md px-4 py-2 font-medium hover:bg-fd-accent">
            <Plug className="size-4" /> Developer docs
          </Link>
          <a href={repoUrl} className="flex items-center gap-2 rounded-md px-4 py-2 font-medium hover:bg-fd-accent">
            GitHub
          </a>
        </nav>
      </section>

      <section className="mt-16 grid w-full max-w-5xl gap-3 sm:grid-cols-2 lg:grid-cols-4">
        {features.map(({ icon: Icon, title, text, chips }) => (
          <div key={title} className="xiv-card flex flex-col gap-2 p-4">
            <span className="flex size-9 items-center justify-center rounded-lg bg-fd-accent text-fd-primary">
              <Icon className="size-[18px]" />
            </span>
            <h2 className="mt-1 text-sm font-semibold">{title}</h2>
            <p className="text-sm text-fd-muted-foreground">{text}</p>
            <div className="mt-auto flex flex-wrap gap-1.5 pt-2">
              {chips.map((c) => (
                <span key={c} className="xiv-chip">
                  {c}
                </span>
              ))}
            </div>
          </div>
        ))}
      </section>

      <section className="mt-16 grid w-full max-w-5xl gap-3 md:grid-cols-3">
        {[
          ['1', 'Install the plugin', 'Add XIV MCP in the Dalamud plugin installer.', '/docs/user/install'],
          ['2', 'Connect your AI app', 'One click in /xivmcp → Connect for most apps.', '/docs/user/connect'],
          ['3', 'Choose what it may do', 'Allow, ask or deny, per module and per tool.', '/docs/user/permissions/modules'],
        ].map(([n, title, text, href]) => (
          <Link key={n} href={href} className="xiv-card flex items-start gap-4 p-4">
            <span className="xiv-gradient-text text-3xl font-bold leading-none">{n}</span>
            <span>
              <span className="block text-sm font-semibold">{title}</span>
              <span className="block text-sm text-fd-muted-foreground">{text}</span>
            </span>
          </Link>
        ))}
      </section>

      <section className="mt-16 flex w-full max-w-5xl items-start gap-4 rounded-xl border border-fd-border p-5 text-sm text-fd-muted-foreground">
        <ShieldCheck className="mt-0.5 size-5 shrink-0 text-fd-primary" />
        <p>
          The server only answers on your own PC and needs an access token. Every tool that acts in the game, edits something or
          goes online is off until you allow it, and anything set to <em>Ask</em> waits for you in the game. Automating game
          actions is against the FFXIV terms of service: use XIV MCP at your own risk.
        </p>
      </section>
    </main>
  );
}

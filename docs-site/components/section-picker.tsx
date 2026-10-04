import Link from 'next/link';
import { Gamepad2, Puzzle } from 'lucide-react';

const sections = [
  {
    href: '/docs/user',
    icon: Gamepad2,
    title: 'User docs',
    text: 'Install XIV MCP, connect your AI app, choose what it may do, and find out what to ask.',
    chips: ['Install', 'Connect', 'Permissions', 'Tools'],
  },
  {
    href: '/docs/developer',
    icon: Puzzle,
    title: 'Developer docs',
    text: 'Offer your own plugin to AI assistants through the plugin API, or build and improve XIV MCP itself.',
    chips: ['Plugin API', 'Jobs', 'IPC', 'Contributing'],
  },
];

export function SectionPicker() {
  return (
    <div className="not-prose grid gap-4 sm:grid-cols-2">
      {sections.map(({ href, icon: Icon, title, text, chips }) => (
        <Link key={href} href={href} className="xiv-card flex flex-col gap-3 p-5 no-underline">
          <span className="flex size-10 items-center justify-center rounded-lg bg-fd-accent text-fd-primary">
            <Icon className="size-5" />
          </span>
          <span className="text-base font-semibold text-fd-foreground">{title}</span>
          <span className="text-sm text-fd-muted-foreground">{text}</span>
          <span className="mt-auto flex flex-wrap gap-1.5 pt-1">
            {chips.map((c) => (
              <span key={c} className="xiv-chip">
                {c}
              </span>
            ))}
          </span>
        </Link>
      ))}
    </div>
  );
}

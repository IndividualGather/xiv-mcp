// Generates content/docs/user/tools.mdx (and user/plugins.mdx, see below) from XIV MCP's own catalogs, so the tools reference always matches the code:
//   XivMcp.Core/Permissions/PermissionCatalog.cs   groups, their defaults and which core tool belongs to which
//   XivMcp.Core/Integrations/IntegrationCatalog.cs  the integrations and the capabilities of their tools
//   XivMcp.Core/Mcp/ToolSummaries.cs                the one-sentence description of each tool for players
//   XivMcp/Tools/*.cs                               which core tools change something (ReadOnly = false)
// Runs before `npm run dev` and `npm run build`. The output is not committed.
import { readFileSync, readdirSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const site = join(dirname(fileURLToPath(import.meta.url)), '..');
const repo = join(site, '..');
const read = (path) => readFileSync(join(repo, path), 'utf8');
const out = join(site, 'content', 'docs', 'user', 'tools.mdx');

const warnings = [];

// ToolSummaries: ["name"] = "text",
const summaries = new Map(
  [...read('XivMcp.Core/Mcp/ToolSummaries.cs').matchAll(/\["(\w+)"\] = "((?:[^"\\]|\\.)*)",/g)].map((m) => [m[1], m[2].replace(/\\"/g, '"')]),
);

// PermissionCatalog: the core groups, then the tool lists per group.
const catalog = read('XivMcp.Core/Permissions/PermissionCatalog.cs');
const mode = { A: 'Allow', K: 'Ask', D: 'Deny' }; // K for asK, as in PermissionCatalog
const groups = [
  ...catalog.matchAll(/new\("(\w+)", "([^"]+)", "([^"]+)",\s*([AKD]), ([AKD])((?:, Has\w+: false)*)\)/g),
].map((m) => ({
  id: m[1],
  title: m[2],
  description: m[3],
  read: m[6].includes('HasRead: false') ? null : mode[m[4]],
  write: m[6].includes('HasWrite: false') ? null : mode[m[5]],
  tools: [],
}));
// DevTools: only offered in development builds, so they stay out of the player docs.
const devToolList = catalog.match(/DevTools \{ get; \} = new HashSet<string> \{([^}]*)\}/);
if (!devToolList) warnings.push('PermissionCatalog.DevTools was not found; dev-only tools may show up in the docs.');
const devTools = new Set([...(devToolList?.[1] ?? '').matchAll(/"(\w+)"/g)].map((t) => t[1]));
const coreTools = catalog.slice(catalog.indexOf('CoreTools'));
for (const m of coreTools.matchAll(/\["(\w+)"\] =\s*\[([^\]]*)\]/g)) {
  const group = groups.find((g) => g.id === m[1]);
  if (!group) {
    warnings.push(`PermissionCatalog lists tools for an unknown group '${m[1]}'.`);
    continue;
  }
  group.tools = [...m[2].matchAll(/"(\w+)"/g)].map((t) => t[1]).filter((t) => !devTools.has(t));
}

// Core tools that change something: their definitions set ReadOnly = false (it defaults to true). Most are `Name = "x", …`;
// a few are made in a loop over `("x", …)` tuples with `Name = someVariable`, and then the loop's ReadOnly applies to each.
const changing = new Set();
const defined = new Set();
const sources = readdirSync(join(repo, 'XivMcp'), { recursive: true })
  .filter((f) => f.endsWith('.cs') && !/[\\/](bin|obj)[\\/]/.test(f))
  .map((f) => read(join('XivMcp', f)));
for (const code of sources) {
  for (const part of code.split(/\bName = "/).slice(1)) {
    const name = part.slice(0, part.indexOf('"'));
    defined.add(name);
    if (/\bReadOnly = false\b/.test(part.split(/\bName = /)[0])) changing.add(name);
  }
  for (const loop of code.matchAll(/foreach \(var \((\w+),[^)]*\) in new\[\]\s*\{([\s\S]*?)\}\)\s*\{([\s\S]*?)\n\s{8}\}/g)) {
    const [, variable, tuples, body] = loop;
    if (!new RegExp(`\\bName = ${variable}\\b`).test(body)) continue;
    for (const t of tuples.matchAll(/\(\s*"(\w+)"/g)) {
      defined.add(t[1]);
      if (/\bReadOnly = false\b/.test(body)) changing.add(t[1]);
    }
  }
}

// IntegrationCatalog: new("PluginId", "Display name", "Summary", ...{ ["tool"] = [Capabilities], ... })
const integrations = [];
const integrationSource = read('XivMcp.Core/Integrations/IntegrationCatalog.cs');
for (const m of integrationSource.matchAll(/new\("(\w+)", "([^"]+)", "([^"]+)", new Dictionary<string, string\[\]>\s*\{([^}]*)\}/g)) {
  const tools = [...m[4].matchAll(/\["(\w+)"\] = \[([^\]]*)\]/g)].map((t) => ({
    name: t[1],
    capabilities: t[2].split(',').map((c) => c.trim()).filter(Boolean).map(snake),
  }));
  // After the tool list: { AlsoWith = { ["tool"] = ["Other"] }, Helpers = { ["tool"] = Travel } } up to the next integration.
  const rest = integrationSource.slice(m.index + m[0].length);
  const extras = rest.slice(0, rest.search(/\n\s*new\("|\n\s*\];/));
  const block = (name) => extras.match(new RegExp(`${name} = new Dictionary<[^>]+>\\s*\\{([\\s\\S]*?)\\n\\s*\\},`))?.[1] ?? '';
  const alsoWith = Object.fromEntries([...block('AlsoWith').matchAll(/\["(\w+)"\] = \[([^\]]*)\]/g)].map((a) => [a[1], [...a[2].matchAll(/"(\w+)"/g)].map((x) => x[1])]));
  const helpers = Object.fromEntries([...block('Helpers').matchAll(/\["(\w+)"\] = (\w+)/g)].map((h) => [h[1], h[2]]));
  // Standalone = new HashSet<string> { "tool", ... }: tools of the integration that need no plugin.
  const standalone = new Set([...(extras.match(/Standalone = new HashSet<string> \{([^}]*)\}/)?.[1] ?? '').matchAll(/"(\w+)"/g)].map((x) => x[1]));
  integrations.push({ id: m[1], title: m[2], description: m[3], tools, alsoWith, helpers, standalone });
}
// Helper lists named in Helpers: private static ToolRequirement[] Travel => [ new("vnavmesh", Need.Improves, "purpose", "without"), ... ];
const helperLists = Object.fromEntries([...integrationSource.matchAll(/ToolRequirement\[\] (\w+) =>\s*\[([\s\S]*?)\];/g)].map((h) => [
  h[1],
  [...h[2].matchAll(/new\("(\w+)", Need\.\w+, "((?:[^"\\]|\\.)*)", "((?:[^"\\]|\\.)*)"\)/g)].map((x) => ({ plugin: x[1], purpose: x[2], without: x[3] })),
]));

function snake(pascal) {
  return pascal.replace(/([a-z])([A-Z])/g, '$1_$2').toLowerCase();
}

if (groups.length === 0 || integrations.length === 0) throw new Error('generate-tools: could not read the catalogs; did their format change?');

// MDX treats { } < > as syntax; summaries are plain text.
const text = (s) => s.replace(/[{}<>]/g, (c) => `\\${c}`);
const summary = (name) => {
  const s = summaries.get(name);
  if (!s) warnings.push(`'${name}' has no entry in ToolSummaries.`);
  return s ? text(s) : '';
};
const chip = (label, value) => `<span className="xiv-chip">${label}: ${value}</span>`;

const lines = [];
const total = groups.reduce((n, g) => n + g.tools.length, 0) + integrations.reduce((n, i) => n + i.tools.length, 0);
lines.push(
  '---',
  'title: Tools reference',
  `description: All ${total} tools XIV MCP offers your assistant, by module, with what each one does.`,
  'icon: Wrench',
  '---',
  '',
  '{/* Generated by scripts/generate-tools.mjs from the C# catalogs on every build. Do not edit: change the code instead. */}',
  '',
  'Your assistant picks these tools on its own; you never call them yourself. This list shows what it can use, so you know what to ask for and which setting a tool follows.',
  '',
  '- **Reads** tools only look. They follow the *Reading* setting of their module (Allow, Ask or Deny).',
  '- **Changes** tools act in the game or edit something. They follow the *Changes* setting.',
  '- The chips under each module show its default settings. You change them in `/xivmcp` → **Modules**, see [Modules and permissions](/docs/user/permissions/modules).',
  '',
  '<Callout type="info">This page is generated from XIV MCP\'s own code on every build, so it always matches the version the documentation was built from.</Callout>',
  '',
  'The same list is in the game: `/xivmcp` → **Info** → **Tools**, with a filter, and with the tools of third-party plugins you enabled.',
  '',
  '<Screenshot src="info-tools.png" alt="The Tools page of the Info tab: a filter field and a table of tools with access, source and what each does" caption="/xivmcp → Info → Tools." />',
  '',
);

lines.push('## Core modules', '');
for (const g of groups) {
  lines.push(`### ${g.title}`, '', g.description.replace(/\.$/, '') + '.', '');
  const chips = [g.read && chip('Reading', g.read), g.write && chip('Changes', g.write)].filter(Boolean);
  lines.push(`<div className="not-prose flex flex-wrap gap-1.5">${chips.join(' ')}</div>`, '');
  lines.push('| Tool | Kind | What it does |', '|---|---|---|');
  for (const t of g.tools) {
    if (!defined.has(t)) warnings.push(`'${t}' is in PermissionCatalog but no definition was found in XivMcp/Tools.`);
    lines.push(`| \`${t}\` | ${changing.has(t) ? 'Changes' : 'Reads'} | ${summary(t)} |`);
  }
  lines.push('');
}

lines.push(
  '## Integrations',
  '',
  'These modules drive one other plugin each. They are only offered while that plugin is loaded, and they have their own settings, independent of the core modules. A tool that changes something lists the [capabilities](/docs/developer/plugin-api/capabilities) it uses.',
  '',
);
for (const i of integrations) {
  const hasRead = i.tools.some((t) => t.capabilities.length === 0);
  const hasWrite = i.tools.some((t) => t.capabilities.length > 0);
  lines.push(`### ${i.title}`, '', i.description, '');
  const chips = [hasRead && chip('Reading', 'Allow'), hasWrite && chip('Changes', 'Deny')].filter(Boolean);
  lines.push(`<div className="not-prose flex flex-wrap gap-1.5">${chips.join(' ')}</div>`, '');
  lines.push('| Tool | Kind | What it does |', '|---|---|---|');
  for (const t of i.tools) {
    const kind = t.capabilities.length ? `Changes (${t.capabilities.map((c) => `\`${c}\``).join(', ')})` : 'Reads';
    lines.push(`| \`${t.name}\` | ${kind} | ${summary(t.name)} |`);
  }
  lines.push('');
}

lines.push(
  '## Tools from other plugins',
  '',
  'Plugins you install can add their own tools. They appear in `/xivmcp` → **Third-party plugins** once you enable them, not in this list. See [Third-party plugins](/docs/user/permissions/third-party).',
  '',
);

writeFileSync(out, lines.join('\n'));

// ------------------------------------------------------------------ tools that need plugins
// XivMcp.Core/Integrations/PluginCatalog.cs     the plugins and where to install them
// XivMcp.Core/Integrations/ToolRequirements.cs  which core tools use which plugin, and what for

const catalogSource = read('XivMcp.Core/Integrations/PluginCatalog.cs');
const known = new Map(
  [...catalogSource.matchAll(/new\("(\w+)", "([^"]+)", (?:"([^"]+)"|Official)\)/g)].map((m) => [
    m[1].toLowerCase(),
    { id: m[1], name: m[2], repo: m[3] ?? 'official' },
  ]),
);
const plugin = (id) => {
  const p = known.get(id.toLowerCase());
  if (!p) warnings.push(`'${id}' is used by a tool but missing from PluginCatalog.`);
  return p ?? { id, name: id, repo: 'official' };
};

const requirementSource = read('XivMcp.Core/Integrations/ToolRequirements.cs');
// Texts shared by several entries are constants: private const string Walk = "…";
const constants = new Map(
  [...requirementSource.matchAll(/const string (\w+) = "((?:[^"\\]|\\.)*)";/g)].map((m) => [m[1], m[2].replace(/\\"/g, '"')]),
);
const textArg = (raw) => {
  const literal = raw.match(/^"((?:[^"\\]|\\.)*)"$/);
  if (literal) return literal[1].replace(/\\"/g, '"');
  if (!constants.has(raw)) warnings.push(`ToolRequirements uses '${raw}', which is not a string constant.`);
  return constants.get(raw) ?? raw;
};
const arg = String.raw`("(?:[^"\\]|\\.)*"|\w+)`;
const entry = new RegExp(String.raw`new\("(\w+)", ([NI]),\s*` + arg + String.raw`,\s*` + arg + String.raw`\)`, 'g');
const coreBlock = requirementSource.slice(requirementSource.indexOf('Core {'));
const requirements = [...coreBlock.matchAll(/\["(\w+)"\] =\s*\[([\s\S]*?)\]\s*,/g)].map((m) => ({
  tool: m[1],
  uses: [...m[2].matchAll(entry)].map((n) => ({
    plugin: plugin(n[1]),
    purpose: textArg(n[3]),
    without: textArg(n[4]),
  })),
}));
if (known.size === 0 || requirements.length === 0 || requirements.some((r) => r.uses.length === 0))
  throw new Error('generate-tools: could not read PluginCatalog or ToolRequirements; did their format change?');

const repoCell = (r) => (r === 'official' ? "Dalamud's main repository" : `<span className="xiv-url">${r}</span>`);
const req = [];
req.push(
  '---',
  'title: Plugins XIV MCP uses',
  "description: Which of XIV MCP's tools use other plugins, what happens without them, and where to get each plugin.",
  'icon: Blocks',
  '---',
  '',
  '{/* Generated by scripts/generate-tools.mjs from PluginCatalog.cs, ToolRequirements.cs and IntegrationCatalog.cs on every build. */}',
  '',
  'You do not need any other plugin to use XIV MCP. Its tools come in two kinds:',
  '',
  '- **Core tools work without any other plugin.** Some of them hand a part of the work to a plugin when you have it: walking, travel, finding vendors. Without it, XIV MCP asks you to do that part, and waits until you have.',
  '- **Integration tools drive one plugin each,** and need it. Your assistant only sees them while that plugin is loaded.',
  '',
  "Plugin developers: when your jobs use one of these tools, declare it with `McpDependency.BuiltIn(\"tool\")`. See [Using other tools in your jobs](/docs/developer/plugin-api/interop).",
  '',
  '<Callout type="info">This page is generated from XIV MCP\'s own code on every build, so it always matches the version the documentation was built from.</Callout>',
  '',
  '## Core tools that use plugins',
  '',
  'Every other core tool uses no plugin at all.',
  '',
  '| Tool | What it does | With the plugin | Without it |',
  '|---|---|---|---|',
);
for (const r of requirements) {
  const withIt = r.uses.map((u) => `**${u.plugin.name}:** ${text(u.purpose)}`).join('<br />');
  const without = r.uses.map((u) => `**No ${u.plugin.name}:** ${text(u.without)}`).join('<br />');
  req.push(`| \`${r.tool}\` | ${summary(r.tool)} | ${withIt} | ${without} |`);
}
req.push(
  '',
  '## Integration tools',
  '',
  'These tools exist to drive one plugin each, so they always need it. They are only offered to the assistant while that plugin is loaded.',
  '',
);
for (const i of integrations) {
  const extra = i.tools.some((t) => i.alsoWith[t.name] || i.helpers[t.name] || i.standalone.has(t.name));
  req.push(`### ${i.title}`, '', `${text(i.description)} Needs **${plugin(i.id).name}**${extra ? ', except where the table says otherwise' : ''}.`, '');
  if (!extra) {
    req.push('| Tool | What it does |', '|---|---|');
    for (const t of i.tools) req.push(`| \`${t.name}\` | ${summary(t.name)} |`);
  } else {
    req.push('| Tool | What it does | Plugins |', '|---|---|---|');
    for (const t of i.tools) {
      const needs = [i.id, ...(i.alsoWith[t.name] ?? [])].map((p) => `**${plugin(p).name}**`).join(' or ');
      const helps = (helperLists[i.helpers[t.name]] ?? []).map((h) => `${plugin(h.plugin).name}: ${text(h.purpose)} Without it: ${text(h.without)}`);
      const needsText = i.standalone.has(t.name) ? 'Needs no plugin.' : `Needs ${needs}.`;
      req.push(`| \`${t.name}\` | ${summary(t.name)} | ${needsText}${helps.length ? '<br />' + helps.join('<br />') : ''} |`);
    }
  }
  req.push('');
}
req.push(
  '## Where to get them',
  '',
  "Plugins in Dalamud's main repository are installed from `/xlplugins` right away. For the others, add their repository first:",
  '',
  '<Steps>',
  '<Step>',
  '',
  'Type `/xlsettings` and open the **Experimental** tab.',
  '',
  '</Step>',
  '<Step>',
  '',
  'Under **Custom Plugin Repositories**, paste the repository URL from the table, tick **Enabled** and click **Save and Close**.',
  '',
  '</Step>',
  '<Step>',
  '',
  'Type `/xlplugins`, search for the plugin and click **Install**.',
  '',
  '</Step>',
  '</Steps>',
  '',
  'Where XIV MCP shows a missing plugin, such as under *Market & purchases* or on a third-party plugin\'s card, an **Install** button opens the plugin installer at that plugin. For a plugin outside the main repository, the card also has **Copy repository URL** and **Open Dalamud settings**. Only add repositories you trust.',
  '',
  '| Plugin | Internal name | Repository |',
  '|---|---|---|',
  ...[...known.values()].map((p) => `| ${p.name} | \`${p.id}\` | ${repoCell(p.repo)} |`),
  '',
);
const reqOut = join(site, 'content', 'docs', 'user', 'plugins.mdx');
writeFileSync(reqOut, req.join('\n'));

for (const w of warnings) console.warn(`generate-tools: ${w}`);
console.log(`generate-tools: wrote ${total} tools to ${out.slice(site.length + 1)}`);
console.log(`generate-tools: wrote ${requirements.length} core tools that use plugins and ${integrations.length} integrations to ${reqOut.slice(site.length + 1)}`);

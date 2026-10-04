import type { ThemeRegistrationRaw } from 'shiki';

// Code colours in the XIV MCP brand: teal for names and keys, blue for strings and calls, violet for keywords, amber for values.
const teal = '#3befc4';
const blue = '#7aa7ff';
const lightBlue = '#a9c4ff';
const violet = '#c4a7ff';
const amber = '#f2c078';
const text = '#e0e0e0';
const muted = '#8b90a0';

export const xivMcpDark: ThemeRegistrationRaw = {
  name: 'xiv-mcp-dark',
  type: 'dark',
  colors: { 'editor.background': '#141518', 'editor.foreground': text },
  settings: [
    { settings: { foreground: text, background: '#141518' } },
    { scope: ['comment', 'punctuation.definition.comment'], settings: { foreground: muted, fontStyle: 'italic' } },
    { scope: ['string', 'string.quoted', 'punctuation.definition.string'], settings: { foreground: lightBlue } },
    { scope: ['constant.numeric', 'constant.language', 'constant.character.escape'], settings: { foreground: amber } },
    { scope: ['keyword', 'storage.type', 'storage.modifier', 'keyword.control'], settings: { foreground: violet } },
    { scope: ['keyword.operator', 'punctuation.accessor'], settings: { foreground: '#c9d1e6' } },
    {
      scope: ['entity.name.type', 'entity.name.class', 'entity.name.namespace', 'support.type', 'support.class', 'storage.type.cs', 'entity.other.inherited-class'],
      settings: { foreground: teal },
    },
    { scope: ['entity.name.function', 'support.function', 'meta.function-call', 'entity.name.function.member'], settings: { foreground: blue } },
    { scope: ['variable', 'variable.parameter', 'variable.other'], settings: { foreground: text } },
    { scope: ['variable.other.property', 'variable.other.object.property', 'entity.name.variable.property'], settings: { foreground: '#d7e3ff' } },
    // JSON and YAML keys, TOML keys, HTML-ish tags
    {
      scope: ['support.type.property-name', 'support.type.property-name.json', 'meta.object-literal.key', 'entity.name.tag', 'keyword.key.toml', 'support.type.property-name.toml'],
      settings: { foreground: teal },
    },
    { scope: ['punctuation', 'meta.brace', 'punctuation.separator', 'punctuation.terminator'], settings: { foreground: '#a3a3a8' } },
    { scope: ['entity.name.section.markdown', 'markup.heading'], settings: { foreground: teal, fontStyle: 'bold' } },
  ],
};

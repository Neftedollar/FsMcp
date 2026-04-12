import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'FsMcp',
  tagline: 'Build MCP servers in F# with type safety and zero boilerplate',
  favicon: 'img/favicon.ico',

  url: 'https://neftedollar.com',
  baseUrl: '/FsMcp/',

  organizationName: 'Neftedollar',
  projectName: 'FsMcp',

  onBrokenLinks: 'throw',
  onBrokenMarkdownLinks: 'warn',

  i18n: {
    defaultLocale: 'en',
    locales: ['en'],
  },

  presets: [
    [
      'classic',
      {
        docs: {
          sidebarPath: './sidebars.ts',
          editUrl: 'https://github.com/Neftedollar/FsMcp/tree/main/website/',
        },
        blog: false,
        theme: {
          customCss: './src/css/custom.css',
        },
      } satisfies Preset.Options,
    ],
  ],

  themes: [
    [
      require.resolve("@easyops-cn/docusaurus-search-local"),
      {
        hashed: true,
        language: ["en"],
        highlightSearchTermsOnTargetPage: true,
        explicitSearchResultPath: true,
        indexBlog: false,
      },
    ],
  ],

  headTags: [
    {
      tagName: 'link',
      attributes: { rel: 'mcp-actions', href: '/FsMcp/mcp-actions.json' },
    },
    {
      tagName: 'link',
      attributes: { rel: 'alternate', type: 'text/plain', href: '/FsMcp/llms.txt', title: 'LLMs.txt' },
    },
    // SEO: canonical description
    {
      tagName: 'meta',
      attributes: {
        name: 'description',
        content: 'FsMcp is an F# toolkit for building Model Context Protocol (MCP) servers and clients with type safety, computation expressions, and zero boilerplate. Wraps the official Microsoft .NET MCP SDK.',
      },
    },
    // SEO: keywords
    {
      tagName: 'meta',
      attributes: {
        name: 'keywords',
        content: 'FsMcp, F#, MCP, Model Context Protocol, .NET, computation expressions, typed tools, MCP server, MCP client, FSharp',
      },
    },
    // Open Graph
    {
      tagName: 'meta',
      attributes: { property: 'og:type', content: 'website' },
    },
    {
      tagName: 'meta',
      attributes: { property: 'og:title', content: 'FsMcp — F# toolkit for Model Context Protocol' },
    },
    {
      tagName: 'meta',
      attributes: {
        property: 'og:description',
        content: 'Build MCP servers and clients in F# with type safety, computation expressions, and zero boilerplate.',
      },
    },
    {
      tagName: 'meta',
      attributes: { property: 'og:url', content: 'https://neftedollar.com/FsMcp/' },
    },
    // Twitter Card
    {
      tagName: 'meta',
      attributes: { name: 'twitter:card', content: 'summary' },
    },
    // Structured Data: SoftwareSourceCode (JSON-LD)
    {
      tagName: 'script',
      attributes: { type: 'application/ld+json' },
      innerHTML: JSON.stringify({
        '@context': 'https://schema.org',
        '@type': 'SoftwareSourceCode',
        name: 'FsMcp',
        description: 'F# toolkit for building Model Context Protocol (MCP) servers and clients with type safety, computation expressions, and zero boilerplate.',
        codeRepository: 'https://github.com/Neftedollar/FsMcp',
        programmingLanguage: 'F#',
        runtimePlatform: '.NET 10',
        license: 'https://opensource.org/licenses/MIT',
        operatingSystem: 'Cross-platform',
      }),
    },
  ],

  themeConfig: {
    navbar: {
      title: 'FsMcp',
      items: [
        {
          type: 'docSidebar',
          sidebarId: 'docs',
          position: 'left',
          label: 'Docs',
        },
        {
          href: 'https://github.com/Neftedollar/FsMcp',
          label: 'GitHub',
          position: 'right',
        },
        {
          href: 'https://www.nuget.org/packages?q=FsMcp',
          label: 'NuGet',
          position: 'right',
        },
      ],
    },
    footer: {
      style: 'dark',
      links: [
        {
          title: 'Docs',
          items: [
            { label: 'Getting Started', to: '/docs/getting-started' },
            { label: 'Server Guide', to: '/docs/server-guide' },
            { label: 'API Reference', to: '/docs/types-reference' },
          ],
        },
        {
          title: 'Community',
          items: [
            { label: 'GitHub Issues', href: 'https://github.com/Neftedollar/FsMcp/issues' },
            { label: 'Contributing', href: 'https://github.com/Neftedollar/FsMcp/blob/main/CONTRIBUTING.md' },
          ],
        },
      ],
      copyright: `Copyright ${new Date().getFullYear()} FsMcp Contributors. MIT License.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['fsharp', 'bash', 'json'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;

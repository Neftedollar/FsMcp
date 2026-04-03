import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'FsMcp',
  tagline: 'Build MCP servers in F# with type safety and zero boilerplate',
  favicon: 'img/favicon.ico',

  url: 'https://neftedollar.github.io',
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

import {themes as prismThemes} from 'prism-react-renderer';
import type {Config} from '@docusaurus/types';
import type * as Preset from '@docusaurus/preset-classic';

const config: Config = {
  title: 'roslyn-codelens-mcp',
  tagline: 'Semantic code intelligence for .NET codebases via MCP',
  favicon: 'img/favicon.ico',
  future: {
    v4: true,
  },
  url: 'https://marcelroozekrans.github.io',
  baseUrl: '/roslyn-codelens-mcp/',
  organizationName: 'MarcelRoozekrans',
  projectName: 'roslyn-codelens-mcp',
  trailingSlash: false,
  onBrokenLinks: 'warn',
  onBrokenMarkdownLinks: 'warn',
  i18n: {defaultLocale: 'en', locales: ['en']},
  presets: [
    ['classic', {
      docs: {
        routeBasePath: '/',
        sidebarPath: './sidebars.ts',
        editUrl: 'https://github.com/MarcelRoozekrans/roslyn-codelens-mcp/edit/main/docs/site/',
      },
      blog: false,
      theme: {customCss: './src/css/custom.css'},
    } satisfies Preset.Options],
  ],
  themeConfig: {
    navbar: {
      title: 'roslyn-codelens-mcp',
      items: [
        {type: 'docSidebar', sidebarId: 'mainSidebar', position: 'left', label: 'Docs'},
        {
          href: 'https://github.com/MarcelRoozekrans/roslyn-codelens-mcp',
          label: 'GitHub',
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
            {label: 'Getting Started', to: '/getting-started/installation'},
            {label: 'Tool Reference', to: '/tools/navigation/go-to-definition'},
          ],
        },
        {
          title: 'More',
          items: [
            {label: 'GitHub', href: 'https://github.com/MarcelRoozekrans/roslyn-codelens-mcp'},
            {label: 'NuGet', href: 'https://www.nuget.org/packages/RoslynCodeLens.Mcp'},
          ],
        },
      ],
      copyright: `Copyright © ${new Date().getFullYear()} Marcel Roozekrans. Built with Docusaurus.`,
    },
    prism: {
      theme: prismThemes.github,
      darkTheme: prismThemes.dracula,
      additionalLanguages: ['csharp', 'json', 'bash'],
    },
  } satisfies Preset.ThemeConfig,
};

export default config;

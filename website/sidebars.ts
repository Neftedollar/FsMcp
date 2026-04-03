import type {SidebarsConfig} from '@docusaurus/plugin-content-docs';

const sidebars: SidebarsConfig = {
  docs: [
    'intro',
    'getting-started',
    {
      type: 'category',
      label: 'Guides',
      items: [
        'server-guide',
        'middleware-guide',
        'client-guide',
        'testing-guide',
      ],
    },
    {
      type: 'category',
      label: 'Advanced',
      items: [
        'advanced',
        'types-reference',
      ],
    },
  ],
};

export default sidebars;

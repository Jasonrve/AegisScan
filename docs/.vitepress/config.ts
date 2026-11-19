import { defineConfig } from 'vitepress'

export default defineConfig({
  title: 'AegisScan',
  description: 'Kubernetes security automation, built for operators.',
  cleanUrls: true,
  lastUpdated: true,
  base: process.env.GITHUB_ACTIONS ? '/AegisScan/' : '/',
  themeConfig: {
    logo: '/mark.svg',
    siteTitle: 'AegisScan',
    nav: [
      { text: 'Developer', link: '/development/getting-started' },
      { text: 'Architecture', link: '/architecture/overview' },
      { text: 'Operations', link: '/operations/installation' },
      { text: 'GitHub', link: 'https://github.com/Jasonrve/AegisScan' }
    ],
    sidebar: [
      {
        text: 'Start here',
        items: [
          { text: 'Overview', link: '/' },
          { text: 'Getting started', link: '/development/getting-started' },
          { text: 'Local development', link: '/operations/local-development' }
        ]
      },
      {
        text: 'Architecture',
        items: [
          { text: 'System overview', link: '/architecture/overview' },
          { text: 'Data ownership', link: '/architecture/data-ownership' },
          { text: 'Git cache', link: '/architecture/git-cache' },
          { text: 'Scanner plugins', link: '/architecture/scanner-plugins' }
        ]
      },
      {
        text: 'Development',
        items: [
          { text: 'KubeOps migration', link: '/development/kubeops-migration' },
          { text: 'Kubernetes test plan', link: '/development/kubernetes-test-plan' }
        ]
      },
      {
        text: 'Operations',
        items: [
          { text: 'Installation', link: '/operations/installation' },
          { text: 'Configuration', link: '/operations/configuration' },
          { text: 'Scanners', link: '/operations/scanners' },
          { text: 'SBOMs and images', link: '/operations/sbom' },
          { text: 'Identity', link: '/operations/identity' },
          { text: 'CRD upgrades', link: '/operations/crd-upgrades' },
          { text: 'Releases', link: '/operations/releases' },
          { text: 'Runbooks', link: '/operations/runbooks' },
          { text: 'Troubleshooting', link: '/operations/troubleshooting' }
        ]
      },
      {
        text: 'Decisions',
        items: [
          { text: 'Shared Git mirror', link: '/adr/0001-shared-git-mirror' },
          { text: 'Tenancy scope', link: '/adr/0002-tenancy-scope' }
        ]
      }
    ],
    socialLinks: [
      { icon: 'github', link: 'https://github.com/Jasonrve/AegisScan' }
    ],
    footer: {
      message: 'AegisScan documentation',
      copyright: 'Released under the project license.'
    },
    search: { provider: 'local' },
    outline: { level: [2, 3] },
    editLink: {
      pattern: 'https://github.com/Jasonrve/AegisScan/edit/main/docs/:path'
    }
  }
})

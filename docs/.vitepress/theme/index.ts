import DefaultTheme from 'vitepress/theme'
import DocsHero from './DocsHero.vue'
import './custom.css'

export default {
  extends: DefaultTheme,
  enhanceApp({ app }) {
    app.component('DocsHero', DocsHero)
  }
}

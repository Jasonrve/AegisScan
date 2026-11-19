<script setup lang="ts">
import { onMounted, onUnmounted, ref } from 'vue'
import { withBase } from 'vitepress'
import gsap from 'gsap'
import { ScrollTrigger } from 'gsap/ScrollTrigger'

gsap.registerPlugin(ScrollTrigger)

const root = ref<HTMLElement | null>(null)
let context: gsap.Context | undefined

onMounted(() => {
  if (!root.value) return
  context = gsap.context(() => {
    gsap.from('.hero-kicker, .hero-title, .hero-copy, .hero-actions', {
      y: 24,
      opacity: 0,
      duration: 0.8,
      stagger: 0.1,
      ease: 'power3.out'
    })
    gsap.from('.signal-node', {
      scale: 0.4,
      opacity: 0,
      duration: 0.7,
      stagger: 0.08,
      ease: 'back.out(1.6)',
      delay: 0.35
    })
    gsap.to('.signal-orbit', {
      rotate: 360,
      duration: 28,
      repeat: -1,
      ease: 'none'
    })
    gsap.to('.hero-visual', {
      yPercent: 10,
      opacity: 0.45,
      ease: 'none',
      scrollTrigger: {
        trigger: root.value,
        start: 'top top',
        end: 'bottom top',
        scrub: true
      }
    })
  }, root.value)
})

onUnmounted(() => context?.revert())
</script>

<template>
  <section ref="root" class="docs-hero">
    <div class="hero-copy-column">
      <p class="hero-kicker">Security control plane for Kubernetes</p>
      <h1 class="hero-title">Make every repository an observable security boundary.</h1>
      <p class="hero-copy">AegisScan turns repository discovery, scanner execution, and findings into one auditable operator workflow.</p>
      <div class="hero-actions">
        <a class="hero-button hero-button-primary" :href="withBase('/guide/getting-started')">Get started</a>
        <a class="hero-button hero-button-secondary" :href="withBase('/operations/installation')">Deploy to Kubernetes</a>
      </div>
    </div>
    <div class="hero-visual" aria-label="AegisScan control plane topology" role="img">
      <div class="signal-orbit signal-orbit-outer"></div>
      <div class="signal-orbit signal-orbit-inner"></div>
      <div class="signal-core"><span>A</span><small>CONTROL</small></div>
      <i class="signal-node node-one"></i>
      <i class="signal-node node-two"></i>
      <i class="signal-node node-three"></i>
      <i class="signal-node node-four"></i>
      <span class="signal-label label-one">REPOSITORIES</span>
      <span class="signal-label label-two">SCANNERS</span>
      <span class="signal-label label-three">FINDINGS</span>
    </div>
  </section>
</template>

# OADM website

Landing page and user docs, built with [Astro Starlight](https://starlight.astro.build). Published to
https://cacsjep.github.io/oadm/ by `.github/workflows/pages.yml` on pushes to `main` that change `website/`.

```sh
cd website
npm ci
npm run dev       # http://localhost:4321/oadm/
npm run build     # static site in dist/
```

- Pages: `src/content/docs/` (Markdown or MDX), sidebar in `astro.config.mjs`.
- Screenshots: `src/assets/screens/`, rendered by the headless UI tests (`OADM_SCREENSHOT_DIR`).
- Texts follow the "Wording" rule in the repository's `CLAUDE.md`.

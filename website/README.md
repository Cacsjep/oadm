# OADM website

Landing page, user docs and developer guide, built with [Astro Starlight](https://starlight.astro.build). Published to
https://oadm.app/ by `.github/workflows/pages.yml` on pushes to `main` that change the website, `CHANGELOG.md`,
`samples/` or the SDK.

```sh
cd website
npm ci
npm run dev       # http://localhost:4321/
npm run build     # static site in dist/
```

- Pages: `src/content/docs/` (Markdown or MDX), sidebar in `astro.config.mjs`.
- Generated, git-ignored pages: the changelog page from `CHANGELOG.md` (before every `dev` and `build`) and the SDK
  reference in `src/content/docs/api/` (`manage build docs`).
- The tutorial pages show the code of `samples/` as it is: change the sample, not the page.
- Screenshots: `src/assets/screens/`, rendered by the headless UI tests. Run the client and plugin tests with
  `OADM_SCREENSHOT_DIR=<folder>` (for example `./manage.sh test unit`), then `npm run screens -- <folder>` copies
  every image by its name in `scripts/screens.json` (website name: test output name) and names the ones it did not
  find. A new image gets its line there. Look at each image before you commit it.
- Texts follow the "Wording" rule in the repository's `CLAUDE.md`.

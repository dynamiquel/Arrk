# Stackarr website

Static GitHub Pages site for Stackarr. Everything is plain HTML, CSS and a small
vanilla-JavaScript file — no build step.

## Preview locally

```bash
python3 -m http.server 8080 --directory site
# open http://localhost:8080
```

Serve over HTTP rather than opening the files directly so the external SVG icon sprite
works consistently.

## Deploy

The workflow in `.github/workflows/pages.yml` uploads `site/` to GitHub Pages on every
push to `main` that touches the site. In the repository settings, set **Pages → Build and
deployment → Source** to **GitHub Actions**.

## Editing

- `assets/css/site.css` — the design system, theme tokens and page styles.
- `assets/js/site.js` — navigation, copy buttons, scroll reveal, guide scrollspy and FAQ search.
- `assets/icons.svg` — the shared SVG icon sprite.
- `assets/logo.svg`, `assets/favicon.svg` — brand marks.
- `assets/og.png` — the 1200×630 social preview card.
- `robots.txt`, `sitemap.xml` — crawler hints for the canonical Pages URL.

The canonical URL and social metadata assume the site is published at
`https://dynamiquel.github.io/Stackarr/`. Update those absolute URLs if the repository moves.

# EWP Design System

The look of the fictitious organisation **Four Walls Inc.**: colours, typography, radii, shadows and the brand mark. Every front end derives its look from this folder.

| File | Purpose |
|---|---|
| `ewp-tokens.css` | **The single source of values.** Plain CSS variables on `:root` (`--ewp-brand-800`, `--ewp-accent-600`, `--ewp-ink`, …). |
| `ewp-theme.css` | Tailwind v4 mapping (`@theme inline`). Turns the tokens into utilities (`bg-brand-800`, `text-ink-muted`, `border-line`, `rounded-card`, …). |
| `four-walls-mark.svg` | The flat brick-wall mark: navy/teal palette, with one teal "keystone" brick. The older 3D logo (`src/IDP/wwwroot/res/img/FourWallsIncLogo.png`) is kept for reference only. |

| Consumer | How |
|---|---|
| Customer Onboarding MFE, Customer KYC MFE | `app/globals.css` contains `@import "tailwindcss";` followed by an `@import` of `ewp-theme.css`. `next.config.ts` sets `turbopack.root` to `src/` so the import can leave the app folder. |
| IDP (Razor Pages) | An MSBuild target in the `.csproj` copies `ewp-tokens.css` and the mark into `wwwroot` on every build. `wwwroot/css/site.css` styles the pages with `var(--ewp-…)` only. No Bootstrap, no CDN. |
| Shell | Not yet migrated; it can link `ewp-tokens.css` the same way the IDP does. |

**Components.** Each MFE owns a small `app/components/ui/` folder of shadcn/ui-style components (Button, Card, Field, Badge, Alert, …), built with `class-variance-authority` and `tailwind-merge`. They are copied into each app rather than shared as a package, so every MFE still builds and deploys on its own. The tokens keep them consistent.

**Rules.**
- Change the brand only in `ewp-tokens.css`.
- Brick colours (`--ewp-brick-*`) are for the mark only. UI states use success/warning/danger/info.
- System fonts only, so no external font requests (CSP `font-src 'self'`).
- Styles are compiled or served as files. No inline styles and no runtime CSS-in-JS, which keeps the strict Content-Security-Policy intact (the IDP's CSP has no `'unsafe-inline'` at all).

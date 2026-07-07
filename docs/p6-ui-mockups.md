# P6 — UI Mockups (Google Stitch)

Status: **shipped (Stages 1-10 of vertical plan)** — see [`docs/ui-troubleshooting.md`](ui-troubleshooting.md) and [`docs/operator-cookbook.md`](operator-cookbook.md) (Dashboard navigation section) for runtime details.

## Implementation timeline

| Stage | Commit | Scope |
|---|---|---|
| 1 | `3229e2f` | Blazor Server + Fluxor + AppShell scaffold |
| 2 | `e908f05` | `/api/health/heartbeat` + `/api/sprints/active` + `/api/search` |
| 3 | `012404d` | Ops: recovery policies + memory + headroom stats |
| 4 | `dd9377b` | Specs grid + filter chips + action-state matrix |
| 5 | `2d7d4c5` | Designs (Kanban) + Art (gallery) |
| 6 | `e96121c` | Intake (3-pane) + CodebaseGraph rebuild |
| 7 | `eddc33b` | Vision Board (markdown + sticky ToC) |
| 8 | `51efee6` | Sprint proposer (schema v14) |
| 9 | `02b68ed` | Tasks endpoints + project split (`Forge.UI` library) |
| 9.5 | `9455b06` | Fluxor StoreInitializer + BaseAddress + cross-project CSS — bug fixes from live browser testing |
| 10 | `3b380b4` | Backlog / Agents / Skills / Sprints data-bound |

Visual verification for each stage was done in a real Chromium instance (Playwright + Read tool on screenshots). See `/tmp/kilo/forge-*.png` for the artifacts.

## Stitch Project
- Project ID: `5759912549509906396`
- Title: `Forge - AI Coding Agent Orchestrator Dashboard (P6)`
- Design system: `Forge Dark Pro` (`assets/16418354560390329523`)
- DESIGN.md (mobile reference): `projects/5759912549509906396/screens/6640705701347111203`

## Design System: Forge Dark Pro
- **Mode:** Dark, semi-dark (not OLED black)
- **Primary:** `#7C9CFF` (cool periwinkle) — the only saturated hue
- **Tertiary (success):** `#7DDFC1` (mint)
- **Error:** `#FF6B7A` (warm coral)
- **Background:** `#0A0D17`, Surface `#0F1320`, Container `#161A26`
- **Headline/Body font:** Inter; **Mono/Code/Labels:** JetBrains Mono
- **Radius:** 8px default, 4px controls, 12px cards, 9999px pills
- **Density:** 32px row height, 13/20 body, 11/14 label uppercase + 0.04em tracking

## Mockups (8 desktop pages + 1 mobile reference)

Open the Stitch web UI to review visually:
https://stitch.withgoogle.com/projects/5759912549509906396

| # | Page | Screen ID | Stitch title | Dimensions |
|---|------|-----------|--------------|------------|
| 01 | AppShell (Sprints home) | `7e7746c27832405abb48b15170a8b957` | Sprints Dashboard — Forge | 3280×2048 |
| 02 | Intake (3-pane workspace) | `3b72d5a9640d45a184989bf9831bf633` | Intake Workspace — Forge | 2560×2048 |
| 03 | Vision Board (v2 — strict theme) | `5965f5ebf9dc43a0974324b682c1b48f` | Vision Board — Forge Master Design | 2560×2048 |
| 04 | Specs Matrix | `4b8bb3a2bf5a4ebba0d21c2da9f4c8e0` | Specs Matrix — Forge | 2560×2048 |
| 05 | Design Board (Kanban) | `76250a1bd5434282890b26df018fe9ae` | Design Board — Forge | 2560×2048 |
| 06 | Art Gallery (masonry) | `fa80ebc972a24e199d617954ef18f013` | Art Gallery — Forge | 2560×2048 |
| 07 | Proposed Next Sprint (variant) | `c4c38069f93f46e091a7b81a0ee88247` | Proposed Next Sprint — Forge | 2560×2048 |
| 08 | Ops Recovery | `fdb7997175a848d4ac70e15df6d2af20` | Ops — Recovery Dashboard | 2560×2048 |
| R | DESIGN.md mobile preview | `6640705701347111203` | DESIGN.md | 780×1768 |

## Page-by-page summary

### 01 — AppShell (dashboard home showing Sprints)
Top app bar: `Forge` wordmark | global search + `⌘K` | heartbeat dot + "Healthy" | active sprint badge | avatar.
Left nav 240px grouped by phase: **IDEATION** (Intake, Vision Board) · **PRODUCT** (Specs, Designs, Art) · **EXECUTION** (Backlog, Sprints, Tasks) · **OPS** (Agents, Skills, Memory, Cost, Recovery). Active item has 2px periwinkle left border + container-high background.
Right drawer (collapsible) "Live Feed" — collapsed in this mockup.
Main: Sprints page Scrum board (To Do / In Progress / Blocked / Done).

### 02 — Intake (3-pane)
Left 280px: session list with activity timestamps + status dots.
Middle: chat thread; assistant's proposed epic renders as an **elevated Card** with "Accept Epic" primary button + "Refine" outline button.
Right 400px: tabs Spec Draft / Architecture / Memory. Architecture tab active — Cytoscape graph of .NET modules with highlighted nodes for epic touchpoints.

### 03 — Vision Board (v2, regenerated)
Read-focused view of MASTER_DESIGN.md. **v1 was too illustrative and drifted from theme; v2 rebuilt with strict Forge Dark Pro compliance.**
- Top app bar (48px) and left side nav (240px, Vision Board active) fully integrated — this page sits inside the global shell
- Header row: "Vision" h1 + meta subtitle (last revised / size / sections), right side Export (outline) + Trigger Re-plan (primary periwinkle)
- Sticky ToC (240px) inside a `#0F1320` card: 32px rows, active anchor has 2px periwinkle left border + `#1D2230` background
- Document surface (single `#0F1320` card, 32px padding): H1 "Master Design" + meta row in mono, H2 sections (Overview / Pillars / Architecture / Roadmap / Open Questions) with proper 20px Inter 600 hierarchy, body 15/22 Inter 400
- Pillars: 3-column card row, each with `#161A26` icon container + `#7C9CFF` glyph
- Architecture: real code block in `#161A26` surface, JetBrains Mono
- Roadmap: 7-milestone horizontal timeline; P0–P5 mint completed circles, P6 current with periwinkle ring + dot
- Open Questions: 3 list rows with pills — Resolved (mint) ×2, Open (outline slate) ×1

No illustrations, no decorative shapes, pure typography + structure.

### 04 — Specs Matrix
High-density data grid. Columns: ID (mono) · Title · Status (pill) · Version · Parent Epic · Extracted Deps (count badge). 47 rows. One row expanded showing checklist AC + linked task chips + inline action buttons.

### 05 — Design Board
Three-column Kanban: Ready For Design (12) · Needs Revision (8, red left border) · Designed (27). Cards 280px: spec ID + title + 80px wireframe thumbnail + HygieneReport pill row.

### 06 — Art Gallery
Masonry grid with mixed-size cards. Each: 3D asset placeholder + asset name overlay + status badge top-right (SUCCEEDED mint / GENERATING periwinkle / FAILED red). Top error lane for failed runs.

### 07 — Proposed Next Sprint
Two-column. Left 60%: task list with DeterministicScorer math column showing `+10 Priority 1, +5 Theme Match, -20 Downstream Dependency = -5 Total`. Right 40%: scorer breakdown panel + "Why these 7?" audit list. Footer: Commit & Dispatch + Re-score All.

### 08 — Ops Recovery
Recovery tab active. Banner "3 tasks need operator decision before auto-recovery". Table: Task ID · Last Checkpoint · Current Step · Recommended Action (Replay/Skip/Re-claim/Manual) · Risk. Side panel: recovery policy code (mono). Two action buttons.

## API surface cross-check (what each page needs)

| Page | Existing endpoints | Gaps |
|------|---------------------|------|
| AppShell | `/api/health/heartbeat` ❌, `/api/search` ❌, `/api/sprints/active` ❌ | heartbeat, global search, active sprint |
| Intake | `/api/intake/sessions`, `/api/intake/{id}/messages` ❌, `/api/codebase/graph` ❌ | sessions endpoint, chat, codebase graph |
| Vision | reads `MASTER_DESIGN.md` from filesystem | needs an endpoint or static file |
| Specs Matrix | `/api/specs`, `/api/specs/{id}` | row expansion needs AC + linked tasks joined |
| Design Board | `/api/designs`, `/api/specs` | hygiene status joined |
| Art Gallery | `/api/art/outputs` | MeshyTaskRecord joined; download URL |
| Proposed Sprint | `/api/sprints`, `/api/sprints/{id}/candidates` ❌ | scorer breakdown + audit |
| Ops Recovery | `/api/recovery/dryrun` ❌, `/api/recovery/execute` ❌ | recovery policy + execute |

**Two confirmed gaps:** `/api/health/heartbeat` and `/api/search?q=` are not in DashboardHost and need to be added before the AppShell mockup is implementable. The other "❌" items may exist as store methods without HTTP exposure yet — to be verified during implementation.

## Sign-off questions

1. **Layout:** is the 240px left nav + 360px right drawer + 48px top bar acceptable, or do you want a tighter top bar / wider content area?
2. **Density:** 32px rows + 13/20 body in grids. Want 28px rows for more on-screen?
3. **Accent:** keep periwinkle `#7C9CFF` or prefer a different single accent (operator-cool-blue, neutral teal, terminal-green)?
4. **Status semantics:** mint for success, coral for error, periwinkle for active. Confirm or change.
5. **Scope:** do all 8 pages make the cut for P6, or trim to a smaller MVP set (suggest: AppShell + Ops + Specs + Tasks) and defer Vision/Art/Design Board?
6. **Mobile / responsive:** is desktop-only acceptable for v1, or do you want a tablet breakpoint?
7. **API gaps:** approve adding `/api/health/heartbeat` and `/api/search?q=` as the first backend additions before UI work?
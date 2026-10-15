# QuestionAppBuilder — Design

**Date:** 2026-10-15
**Status:** Approved for planning — 2026-10-15. Nothing gets built until the plan is agreed.

## Why this exists

Today, every scored questionnaire/assessment app is a bespoke development project: a developer hand-builds each page, one app at a time. It consumes too much time and too many resources, and every app becomes its own thing to maintain.

QuestionAppBuilder turns that into configuration. An admin defines the app — categories, questions, answer types, scoring — and the platform does the rest: renders it, collects submissions, scores them, reports on them. The org's words for success: easy to create, highly configurable, easy to maintain.

The platform is meant to be consumed, not just run: it ships as versioned NuGet packages that client apps mount, inheriting the host's branding, auth, roles, and database. This is the same operating pattern as TemplateBuilder (startup DI extension, connection string from host config, roles/permissions conventions), but a fully independent codebase — no shared code.

## The three surfaces

Consuming hosts get three mounted surfaces (routes below are defaults; base path configurable per host):

| Surface | Route | Users |
|---|---|---|
| Designer / admin | `/ConfigureApp` | admins / designers |
| Runtime | `/App/{Id}` | people filling a questionnaire |
| Reports | `/Reports` | admins / analysts |

One host holds many apps: one designer surface, many `/App/{Id}`s, one reporting surface across the host's apps. Designing, filling, and report viewing are three permission levels, gated by the host's roles.

## The model

**Application** — created in the designer: name, type, categories.

**Application type** — a lookup table in the consuming app's database, seeded with *Request For Application (RFA)* and *Request For Information (RFI)*. Consumers add their own types. In v1 a type classifies an app; no behavior hangs off it yet. (Owner's decision.)

**Category** — a group of elements. Category-to-tab is 1:1 at runtime: five categories render as five tabs. A category can be marked independent/anonymous.

**Element** — either a scored question or static content (heading, label, instructions — no score).

**Question** — a label plus an answer type; choice types carry their option sets. Answer types seen so far (not the final list): textbox, textarea, dropdown, radio group, checkboxes, number.

**Scoring rules per question** — three decided families:

- presence: answered vs. empty (e.g., filled textbox = 1 point, empty = 0)
- selection: chosen option(s) (e.g., dropdown values in a set = N points, else 0)
- numeric tiers: ranges map to points (e.g., > 35 = 2, 10–25 = 1, < 10 = 0)

Rules are additive: a question carries a list of rules, each contributing points when its condition fires. Per-option points are expressed as one rule per option — no separate model. Multi-select (checkbox) questions support any-of and all-of membership conditions in v1. (Builder's calls.)

There is no right/wrong. A question earns points or earns none; there is no quiz-style correct-answer mode.

**Submission** — created on submit. Stores every answer, points earned per question, totals per category, and the overall total, into the database. Scores are computed and snapshotted at submit time — never recomputed. Each submission freezes the version of the app definition it was answered against, so editing an app later never rewrites history. Definitions are versioned for this reason.

A fill session pins that version when the app is opened; publishing edits mid-fill never disturbs an open session, and new opens get the newest active version. Multiple submissions per user per app are allowed in v1; enforcing one-per-user and letting fillers revisit past results are future/configuration concerns. (Builder's calls.)

## Runtime behavior

- The app opens as tabs, one per category.
- Advancing to the next tab requires the current tab's required fields to be filled (gating). Independent/anonymous categories do not block progression.
- The submit control is configurable (label, etc.).
- After submit, the user sees their result: the score they earned.
- Filling is single-sitting in v1, matching the described flow; save-and-resume is a future subsystem. (Builder's call.)

## Reports

A reporting layer over submission records:

- dashboards and charts, e.g. submission volume over time
- drilldown: open a single submission and see every answer, how it was scored, earned vs. zero per question, and per-category/overall totals

"All possible reports" is the ambition, not v1. A focused first view set gets chosen at planning time and grows later. Email delivery of the result report (to the user and to admins) is explicitly phase 2, not v1.

## Distribution and integration

- Versioned NuGet packages consumed by client apps; the surfaces mount into the host app as Razor Class Libraries (MVC areas, controllers, views) — the proven TemplateBuilder mechanism. ASP.NET Core MVC is the target host stack; Blazor and SPA/API hosts are out of scope for v1. (Confirmed with the owner.)
- The host keeps its own layout, auth, roles, and SQL Server database. Each consuming app has its own database; configuration arrives via connection string and startup wiring.
- Publishing to public nuget.org vs. a private feed is deliberately deferred — it is cheap to flip. What constrains the design is clean embedding from day one.
- Package shape (builder's proposal, mirroring the TemplateBuilder split): `QuestionAppBuilder.Editor` (full designer UI) and `QuestionAppBuilder.Core` (render-only runtime; reports either in Core or split into a third package later). A sample Web host in this repo is the reference implementation. Names are placeholders.

## Constraints

- .NET 8 (LTS) and SQL Server are the stack.
- No shared code with TemplateBuilder; only its proven patterns are reused.
- The public package surface (interfaces, routes, DI extensions) is a compatibility surface; breaking changes are major versions.
- Schema flows through EF Core migrations shipped with the packages.
- The host owns identity and roles; the platform maps onto them and does not invent an auth system.

## Goals

- Creating an app is configuration work, not development work.
- One definition, three surfaces: design once, render everywhere, report on all of it.
- Versioned scoring with immutable submission history.
- Reusable across client apps as versioned packages, without forking.

## Non-goals (v1)

- Emailing result reports (phase 2).
- Publishing to nuget.org (decision deferred; design for it either way).
- Localization (assumed English-only; revisit if needed).
- Save-and-resume drafts; one-per-user submission limits; filler-facing result history. (Builder's calls.)

## Anti-goals

Even a working system fails if:

- creating an app still needs a developer.
- it drifts into a general-purpose no-code platform beyond scored questionnaires.
- reports try to show everything at once instead of answering real questions.
- package upgrades break consuming apps or force data migrations on them unexpectedly.
- history mutates — past submissions or scores recomputed under new rules.

## Left to the builder

These get settled during planning (with the owner only where noted):

1. Final v1 inventory of answer types, their option sets, and validation rules.
2. Exact gating semantics: what "required" means per element type; exact behavior of independent/anonymous categories.
3. Full scoring-rule inventory beyond the three families (negative points? caps? category-level rollups beyond totals?).
4. Definition lifecycle: draft/publish, concurrent editing, audit trail of changes.
5. Concrete role mapping for designer / filler / report viewer, including anonymous fills.
6. The first set of report views.
7. Host integration API: DI extension shape, options object, route mounting, migration strategy into the host's database.
8. Phase-2 email design.

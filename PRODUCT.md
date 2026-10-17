# Product

<!-- impeccable:product-schema 1 -->

> **Current implementation contract:** `docs/superpowers/specs/2026-10-17-questionappbuilder-platform-design.md` supersedes earlier product notes wherever they conflict. The platform now includes durable save/resume, sign-in and email-link access modes, conditional visibility, sequential/free navigation, explicit per-question scoring modes, configurable result visibility, and separately authorized reports. See `docs/integration.md` and `docs/deployment.md` for the implemented integration and operating model.

## Platform

web

## Users

- **External respondents** (primary runtime users): people outside the consuming host organization — applicants, vendors, and similar — answering RFA/RFI-style scored questionnaires via a shared link, with no training and possibly anonymity (the model supports independent/anonymous categories).
- **Admins / designers**: staff at the consuming host who configure categories, questions, answer types, and scoring rules in the designer surface.
- **Admins / analysts**: staff who read submissions and scores in the reporting surface.

## Product Purpose

QuestionAppBuilder turns bespoke scored questionnaire/assessment development into configuration: an admin defines the app — categories, questions, answer types, scoring — and the platform renders it, collects submissions, scores them, and reports on them. It exists because today every scored questionnaire is a one-off development project: too much time and resources, and every app becomes its own thing to maintain. Success in the org's words: easy to create, highly configurable, easy to maintain.

## Positioning

A neighboring product could not truthfully copy this combination:

- **One definition, three surfaces**: design once, render everywhere, report on all of it — designer (`/ConfigureApp`), runtime (`/App/{Id}`), reports (`/Reports`).
- **Versioned definitions with immutable history**: published versions are frozen; a fill pins the version it opened; scores are computed and snapshotted at submit time and never recomputed.
- **Embeds into an existing host**: ships as versioned NuGet packages (MVC/Razor Class Libraries) that mount into the consuming app, inheriting its branding, auth, roles, and SQL Server database. No auth system of its own, no competing identity.
- **Scoring, not quizzing**: a question earns points or earns none — there is no quiz-style right/wrong mode.

## Operating Context

- One host holds many apps: one designer surface, many runtimes, one reporting view across the host's apps; base path is configurable per host.
- Designer flow: define categories → elements (scored questions or static content) → answer types → scoring rules → publish a version; later edits clone a new draft and never disturb published history.
- Runtime flow: a response pins its published version and can be saved and resumed. Categories may be navigated sequentially or freely; visible required answers are enforced at final submission. Conditional visibility hides non-applicable values. Scoring is frozen on submit, and the configured result visibility controls whether the respondent sees confirmation only, total score, or category scores.
- Reports: authorized, version-aware overview, keyset-paged submissions, immutable answer/scoring detail, and bounded CSV exports.
- Seeded application types are Request For Application (RFA) and Request For Information (RFI); consuming orgs add their own types, which classify an app only in v1.
- Consuming orgs run .NET 8 / SQL Server apps with their own migrated databases; configuration arrives via connection string and startup wiring.

## Capabilities and Constraints

Confirmed:

- Three permission levels — designing, filling, report viewing — mapped onto the host's authorization policies. Each application uses host sign-in or account-free email-link access.
- Answer types seen so far (not a final inventory): textbox, textarea, dropdown, radio group, checkboxes, number.
- Three scoring-rule families: presence (answered vs. empty), selection (chosen options; any-of and all-of for checkboxes), numeric comparisons (explicit inclusive/exclusive bounds). Each question uses additive or first-match scoring.
- Multiple distinct responses per person per application are allowed; one response can produce at most one submission.
- Submissions store every answer, per-question points, per-category totals, and the overall total.
- .NET 8 (LTS), SQL Server, EF Core 8; schema flows through EF Core migrations shipped with the packages.
- Host stack: ASP.NET Core MVC with Razor Class Libraries; Blazor and SPA/API hosts are out of scope for v1.
- Public package surface (interfaces, routes, DI extensions) is a compatibility surface: additive changes within a major version.
- No shared code with TemplateBuilder — only its proven patterns are reused.

Deliberately out of scope in the current first release — do not design around them:

- Emailing result reports (phase 2).
- Localization (English-only assumed; revisit if needed).
- One-per-user submission limits; filler-facing submission history; file uploads, signatures, repeating groups, external identifier verification, and arbitrary custom scripts.
- Publishing to nuget.org vs. a private feed (decision deferred; cheap to flip).

Undecided — record as open, do not invent:

- The first concrete set of report views.
- Concrete role mapping for designer / filler / report viewer, including anonymous fills.
- Fine-grained gating semantics per element type.

## Brand Commitments

- Surfaces inherit the host's branding; the platform introduces no visual identity of its own.
- **Professional register is the platform commitment** (owner choice, 2026-10-16, runtime direction round): the standard, enterprise-grade path played straight — no concept metaphors, no theatrical visual worlds. Craft bar for all three surfaces: GOV.UK Forms / GDS, Stripe, and USWDS.
- Product, package, and route names (QuestionAppBuilder, `.Editor` / `.Core`, sample host) are placeholders. No logo, voice, or brand assets exist — do not invent or present presumed ones as real.

## Evidence on Hand

- **Reference questionnaire (owner-supplied):** CT OEC "Early Start CT Endowment Expansion Request for Applications #2" — https://early-start-ct-application.oec.ct.gov/EarlyStartCTExpansion/SiteInfo. Multi-tab scored RFA application (Site Information, Additional Information, Space Types Requests, COI and Child Care Access, Attestation) with yes/no, license-type radio, numeric capacity, address entry with a verify step, and Save & Continue. It remains an anchor scenario; its running score display is reference content, not current product behavior.
- **That reference's visual design is an explicit anti-reference** (owner: not designed well). Use its content and flow as evidence; never its look. This product must do better.
- No testimonials, customers, benchmarks, press, or brand assets exist. Never fabricate them.
- Repo facts: spec at `docs/superpowers/specs/2026-10-15-questionappbuilder-design.md`; implementation plan at `docs/superpowers/plans/2026-10-15-questionappbuilder-core-engine.md`.

## Product Principles

1. **Configuration over development** — creating an app is admin work; if it needs a developer, the product failed.
2. **One definition, three surfaces** — design once, render everywhere, report on all of it.
3. **History is immutable** — published definitions and submitted scores are frozen; later edits never rewrite the past.
4. **Embed cleanly, inherit the host** — identity, roles, branding, and data belong to the host; add capability without competing with it.
5. **Respect the untrained external respondent** — the runtime must guide a stranger to a confident submission, and it never implies right or wrong answers.

## Accessibility & Inclusion

- WCAG 2.1 AA is required for all three surfaces (owner-confirmed).
- Anonymous/independent categories must work for respondents who cannot or should not identify themselves.
- English-only in v1; localization deliberately deferred.

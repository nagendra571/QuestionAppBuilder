# Runtime Surface — Design Spec

**Date:** 2026-10-16
**Surface:** runtime — `{basePath}/App/{Id}` ("one definition, rendered")
**Status:** historical visual-design context. The approved `docs/superpowers/specs/2026-10-17-questionappbuilder-platform-design.md` and current runtime implementation supersede functional decisions in this document, `PRODUCT.md`, and the 2026-10-15 specification. In particular, the current platform supports durable save/resume, host-sign-in or email-link access, conditional visibility, sequential or free navigation, no score disclosure before submission, and configurable result visibility. Visual guidance below applies only where consistent with that contract.

Companion artifacts:

- `DESIGN.md` — the platform visual system (tokens, components, rules). All three surfaces inherit it.
- `.impeccable/surfaces/src-questionappbuilder-core.md` — runtime surface brief with the direction contract.
- `docs/design/comps/runtime-desktop-top-steps.png`, `runtime-desktop-side-rail.png`, `runtime-mobile.png` — north-star comps (each with a prompt sidecar). Comp 1 (top tab row + centered column) is the documented composition; comp 2's side rail is an accepted alternate for wide screens; comp 3 governs small screens.
- `.impeccable/reference/incumbent-*.png` — the reference application (content evidence; its look is the anti-reference).

## 1. Ground rules that bind the design

From the product spec (authoritative) and PRODUCT.md:

- The app opens as tabs, one per category; category-to-tab is 1:1.
- Advancing requires the current tab's required fields; independent/anonymous categories never block progression.
- The submit control is configurable (label per app).
- Scores are computed and snapshotted at submit; the respondent sees their earned score after submit. Nothing is right-or-wrong.
- A fill pins the published version it opened; publishing edits mid-fill never disturbs an open session.
- Single-sitting in v1 (no save-and-resume); multiple submissions per user per app allowed; no auth system of its own.
- The host owns identity, auth, roles, and page chrome; the platform renders only the application interior.
- WCAG 2.1 AA is required.

Open items stay open (do not invent them): fine-grained gating semantics per element type; per-user submission limits; result history for fillers.

## 2. The flow

1. **Open.** The shared link resolves to the active published version. The view renders: host header (untouched), application title + intro content (category 1), the category tab row, the first category's questions, and the pinned action bar.
2. **Fill.** Each question sits as its own panel. Required questions carry a `Required` marker; optional ones say so. Choice questions use standard radios/checkboxes; text and number fields use visible labels; external identifiers (license number) carry the inline Verify action.
3. **Verify (signature).** The license-number field checks asynchronously: idle → checking → verified (green check + timestamp) or not found (guidance: check the digits or the agency; field stays editable). Verification never traps the respondent; it informs.
4. **Advance.** "Save & Continue" posts the category, then either shows the error summary and blocks (a required answer is missing) or moves to the next category, scrolls to top, and marks the completed tab with a check.
5. **Independent / anonymous categories.** The tab and category header say plainly that the section does not block progression (wording per fine-grained semantics, still open). Their content renders in the same grammar; nothing about identity is implied by completion.
6. **Submit.** The last category's action bar carries the configured submit label (default "Submit application") as the primary. Submitting disables the control and states "Submitting…". On failure, an error alert appears in place with a retry action and nothing is lost on screen.
7. **Score reveal.** On success the view becomes the record: "Your score", the earned total explained in one sentence, per-category totals, and a quiet next-steps note (host-configurable copy). No confetti, no animation, no live-score behavior during fill — the reveal is the product's one honest moment of scoring.
8. **Re-open.** A new open starts a new fill against the newest active version; earlier submissions are immutable history (reports surface).

## 3. Screens and states

| View | States to build |
|---|---|
| **Application view** (default) | filling; category complete (tab check); gating blocked (error summary + inline errors, focus to summary); save failure (alert + retry, answers intact); verification states (idle / checking / verified / not found); submit in progress (button busy, form disabled) |
| **Score reveal** | default (after submit); anonymous-category variant (no identity language) |
| **Not available** | unknown app id; no published version; app not accepting fills (plain notice view in host chrome; no dead ends — explains what happened in one sentence) |
| **Anonymized/flat states** | empty help (a question with no help text simply omits the line); a category with only static content renders as panels without question chrome |

Loading is server-rendered; no skeleton theatrics. Long categories scroll with the action bar pinned (desktop) or sticky (mobile).

## 4. Component behavior (system detail in DESIGN.md)

- **Tab row (category navigation):** links, not a widget; current category marked in `#005EA2` with underline; completed categories carry a check glyph; "Category N of 5" context text rides with the row. Mobile: horizontally scrollable strip with the active tab scrolled into view. Keyboard: standard link order; `aria-current` for the active category.
- **Question panel:** label (16/600) → help text (14, `#565C65`) → control; required marker at the label line, right-aligned within the label row; per-panel padding 24px, 16px between panels.
- **Radios / checkboxes:** single column, whole-row hit targets, native semantics (arrow keys, space). Checkbox questions state their membership behavior in help text where any-of/all-of scoring applies.
- **Inputs:** label above, never placeholder-as-label; number inputs state units and bounds in help text when a scoring tier exists ("Licensed capacity, 0–300").
- **Verification field:** input + ghost "Verify" button + status line. Checking disables the button and announces "Checking" politely; verified replaces it with a green check, "Verified", and time; not found keeps the field editable with an error line. States announce via `aria-live="polite"`.
- **Error summary:** on a blocked advance, an error alert renders at the top of the working column, receives focus, and lists each invalid question as an in-page link; the fields carry their own messages in `#B50909`.
- **Action bar:** full-bleed, 1px top border; progress text left ("Category 1 of 5 · 2 required answers left" — counts are live but quiet, tabular figures); secondary "Back" + primary "Save & Continue" right. Mobile: stacked full-width, primary first, sticky.
- **Score reveal:** title (18/700), total as a large tabular number with the plain explanation sentence, per-category list with tabular figures, quiet next-steps note. The total never animates.

## 5. Content inventory

Real captured content from the reference's **Site Information** (use as the anchor scenario; wording preserved where captured, to be reconciled with the live reference when available):

- Intro: "Please complete this application for the site for which you are applying. If you are representing an organization with multiple sites, you will need to complete a separate application for each site you are requesting Early Start CT spaces for. All information provided will be verified for accuracy."
- "Did you attend the pre-bid conference on 10/07/2026?" — radio, Yes/No — note: "This answer will not prevent you from being considered for funding."
- "Is this site licensed by the OEC?" — radio, Yes/No.
- "Please select the type of license this site has:" — radio — DCCC (child day care center), DCGH (group child care home), DCFH (family child care home).
- "Please enter the site's 5-digit license number" — text + Verify.
- Organization name, site name, site address, town/city, ZIP, site contact name, contact email, contact phone.
- "Does this program participate in the Child and Adult Care Food Program (CACFP)?" — radio, Yes/No — with the reference's waiver note (points annotations in the reference are dropped from the runtime by design).
- "What is your site's licensed capacity?" — number.
- Action: "Save & Continue."

Other categories (Additional Information, Space Types Requests, COI and Child Care Access, Attestation): names and flow position are known; verbatim content is **not captured** (the reference gates deep tabs behind a terms acknowledgment). Treat any demo content shown in comps — license numbers, capacities, names — as **synthetic**, and reconcile before shipping. Old comps' help text differing from the reference wording is comp artifact, not content truth.

## 6. Responsive behavior

- **≥1024px:** working column max 760px centered in the ground; header chrome full-bleed; tab row horizontal; action bar pinned to the viewport bottom; content area scrolls between chrome bars (bottom padding ≥ action-bar height).
- **640–1023px:** same structure, gutters 24px; inline control pairs (license number + Verify) stack when under 240px each.
- **<640px:** single column, gutters 16px; tab row becomes a scrollable strip; action bar sticky with stacked full-width buttons (primary on top); touch targets ≥44px; no horizontal scroll at 320px (WCAG 1.4.10).

## 7. Accessibility commitments (WCAG 2.1 AA)

- All inputs labelled; required state programmatically exposed (`aria-required`/`required`) in addition to the visual marker (3.3.2).
- Errors: identified in text, described near the field, and summarized with links on submit attempts (3.3.1, 3.3.3).
- Focus: visible 2px `#2491FF` ring at 2px offset everywhere; never removed (2.4.7).
- Keyboard: complete operation without a pointer; native radio/checkbox key behavior; error summary focus management (2.1.1).
- Contrast: tokens were chosen for AA on their pairings (Action Blue on white, Ink on white, Error Red on white); verify each pairing at implementation (1.4.3, 1.4.11 for control borders).
- Reflow at 320px; text spacing changes do not break layout (1.4.10, 1.4.12).
- Status messages (checking/verified/saved/submitted) are programmatically announced (4.1.3); no time limits on answers.

## 8. Deviations from the reference (deliberate)

| Reference behavior/look | Our design |
|---|---|
| Live "Total sum of score" during fill | No score until submit; reveal is the only scoring moment |
| Red "Verify" button | Verify is a secondary outline action, not a warning |
| Justified bold body text, `XXXXX` placeholders | Plain 15px text, real labels, left-aligned |
| Overlay footer bar floating over content | Proper pinned/sticky action bar with reserved space |
| Dense mixed styling on one screen | One system: panels, single action, meaningful color only |

## 9. Handoff notes

- Build against `DESIGN.md` tokens and components; the runtime ships in the Core package as an RCL view set mounted by the host at the configured base path.
- Components are standard web controls; any JavaScript is progressive enhancement (verification, busy states) with server-posted fallbacks.
- Provenance: every comp in `docs/design/comps/` carries its embedded prompt and sidecar; reference captures live in `.impeccable/reference/`.
- Unresolved product items (gating semantics, submit label per app, role mapping for anonymous fills) are implemented as configuration points, not hard-coded behavior.

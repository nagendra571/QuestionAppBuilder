# QuestionAppBuilder: complete platform design

Date: 2026-10-17

Status: Approved for implementation planning on 2026-10-17. The owner reviewed this written specification and responded, "Looks good to me so far." Implementation plan review and execution-method selection remain pending.

## 1. Purpose and authority

Enable an organization to create, publish, collect, score, and analyze questionnaire applications through configuration. Completeness and confidence are the primary success criteria: an administrator can verify what will happen, a respondent can finish reliably, and an analyst can explain the recorded results.

The platform supports forms, categories, conditional visibility, validation, scoring, and reporting. It is not an arbitrary application generator. An RFA is the first acceptance scenario, not a special application type with hard-coded behavior.

This specification supersedes conflicting decisions in the 2026-10-15 design and plans, PRODUCT.md, and the earlier runtime surface specification once approved. Those documents remain historical context until reconciled during implementation. Existing visual guidance remains applicable where it does not conflict with this specification.

The repository is development-only. There are no external compatibility or production-data obligations. Changes to schema, routes, and interfaces are permitted where justified; no existing database is to be deleted merely by approving this document.

## 2. Confirmed product decisions

- Develop reusable NuGet libraries and a central client application together. The client consumes the libraries; it does not duplicate their behavior.
- One organization and one SQL Server database per deployment, with many configured applications and users.
- Three surfaces: editor, respondent runtime, and reports.
- Immutable published definitions and submissions; responses pin their definition version.
- Durable save-and-resume in the first release.
- Per-application access mode: host sign-in or account-free email-link access.
- Conditional visibility for questions and categories, based on earlier answers.
- Per-question scoring mode: additive or first-match.
- Sequential or free category navigation; all visible required answers are enforced at submission.
- Configurable respondent results: confirmation only, total, or total with category totals.
- Separate controls for stopping new responses and closing submissions.
- Reporting covers inspection and analysis, not reviewer assignment or approval workflows.

Implementation defaults below make these decisions concrete. They are proposals included in this written review, not claims that every detail was separately approved in conversation.

## 3. Architecture and delivery

Use a modular ASP.NET Core MVC application, deployed together. There is no current need for independently deployed services.

| Component | Responsibility |
| --- | --- |
| Domain | Definition and response concepts, rule semantics, lifecycle invariants |
| Application | Authoring, publication, visibility, validation, scoring, response orchestration, reporting contracts |
| Infrastructure | EF Core SQL Server persistence, migrations, concurrency, durable access-token storage, query implementations |
| Editor RCL | Authoring workspace, preview, publication readiness review |
| Runtime RCL | Response entry, access integration, save/resume, review, submission, permitted results |
| Reporting RCL | Overview, filtered submissions, detail, export |
| Central host | Identity, permission mapping, email delivery, branding, configuration, deployment |

Domain must not depend on persistence or MVC. Application services depend on domain concepts and abstractions. Infrastructure implements persistence abstractions. UI projects call application services; host startup wires implementations. This is a responsibility map, not a requirement that Domain depend on Infrastructure.

Keep the existing layered project structure where sound. Exact assembly names and public signatures are settled in the implementation plan. Reports are a separately selectable surface rather than automatically exposed with respondent functionality.

Use project references during development. Verify release artifacts through a separate host referencing built NuGet packages, including packaged views and static assets. The central host must not contain questionnaire business rules.

The current repository targets net8.0 with EF Core 8. Preserve that verified baseline in this design; the pasted TemplateBuilder net8.0/net10.0 context is not evidence of current multi-targeting. Validate framework support and select any additional targets explicitly during planning. Compile and test every target actually shipped. Use SQL Server and EF migrations; do not introduce a second production provider or hand-maintained schema scripts.

Default surface prefixes remain /ConfigureApp, /App, and /Reports, configurable by the host. Host layouts and scoped, overridable style tokens allow branding without global CSS collisions. Host permission policies govern designing, filling, and report viewing; privileged surfaces deny access without appropriate authorization configuration.

## 4. Definition model and publication

An application has a stable identity, type classification, publication pointer, and operational availability. Application types such as RFA/RFI classify applications without special runtime logic.

A version owns its display content, category and element definitions, options, visibility conditions, validation, scoring, navigation mode, access mode, submit label, result visibility, and confirmation instructions. Version-owned settings cannot change after publication. New responses use the active published version; existing responses use their pinned version.

One editable draft per application is sufficient initially. Editing a published application creates a new draft with stable logical keys for corresponding elements and version-specific persisted identities. Cloning remaps conditions and option references to the new graph. Reports remain version-aware even when logical keys match.

Publishing validates the complete graph and atomically publishes the version and switches the current pointer. Conflicting edits and concurrent publishing must fail clearly rather than lose changes. A draft revision token changes on any child edit, so publishing cannot silently race with question or rule updates.

Blocking errors include missing labels, invalid answer configuration, empty required option sets, broken references, circular/forward condition references, invalid numeric bounds, and invalid scoring operands. Deterministically detectable overlaps and unreachable rules produce warnings with affected elements and explanations. Warnings require acknowledgment against the exact draft revision; edits invalidate prior acknowledgment. Do not claim to detect every possible logical redundancy.

Published versions referenced by responses or submissions cannot be destructively removed. Record publication and availability changes with actor and UTC timestamp.

## 5. Editor experience

The workspace provides a category/element outline, focused editing area, and access to preview. Administrators can add, reorder, duplicate, and remove draft content. Reordering or removing a referenced question must identify invalidated conditions; invalid drafts may be saved but cannot be published.

Question settings are grouped into Content, Validation, Visibility, and Scoring. Show only settings relevant to the answer type. Static headings and instructions carry neither answers nor points. Treat content as encoded text initially rather than executable HTML.

Initial answer types are textbox, textarea, dropdown, radio group, checkboxes, and number. Provide optional text-length limits, decimal numeric bounds, required state, and selection-count limits where relevant. Required text must contain non-whitespace content; zero is a valid numeric answer; choice answers must reference valid options. Empty optional answers remain valid and receive zero points.

The first-release scope does not silently include file uploads, signatures, repeating groups, external identifier verification, or arbitrary custom scripts. Additions require explicit scope decisions.

Draft autosave displays saving, saved, unsaved/error, and conflict states. Do not publish while local changes are unsaved or unresolved. A failed save retains entered values on screen. Conflict handling explains that a newer draft exists and provides a way to reload without silently discarding local input.

Preview uses the actual runtime renderer and server-side rule semantics, with isolated preview state and no production submission writes. Administrators can exercise navigation, hidden branches, validation, and result modes. A scoring inspector accepts sample answers and displays matching and contributing rules, evaluation order, and totals. The inspector is never available to ordinary respondents.

## 6. Conditional visibility and navigation

Questions and categories may depend on earlier questions in the global category/element order. A category condition can reference only questions preceding that category. A question can reference earlier questions in its own category or prior categories. This creates an acyclic evaluation order.

Initial conditions use a flat group of predicates combined with ALL or ANY. Predicates support answered/unanswered, choice membership (any/all), and numeric comparisons with explicit equality and inclusive/exclusive bounds. Text content matching and arbitrary nested expressions are outside the initial scope.

A hidden source question is treated as unanswered when evaluating dependent conditions. Hidden stored values never trigger visibility. A false category condition hides all its children. Evaluate the graph in order on both client and server; server evaluation is authoritative.

Hidden questions do not block progress, contribute points, or supply submitted answer values. During editing their previous values remain stored with the draft response so restoring visibility can restore the input. Final submission clears hidden draft values and records their not-applicable status, keeping them out of both submission answers and reports.

Navigation mode is version-level: sequential or free. Sequential navigation validates visible required fields before forward progress and prevents direct-link bypasses; earlier sections can be revisited. Free navigation permits any visible category. Both modes validate every visible required question at final submission. Categories hidden by conditions are skipped. Static-only categories remain navigable.

Independent navigation is not anonymity and does not disable required validation. Optional sections use optional questions or explicit visibility conditions.

## 7. Scoring and explanation

Support presence, selection, and numeric rules. Each question explicitly chooses additive or first-match evaluation. Default numeric tiers to first-match and checkbox scoring to additive; the editor displays the selected mode.

Additive sums every matching rule. First-match evaluates the ordered rules and uses only the first matching rule. Unmatched and unanswered questions earn zero. Hidden questions are not applicable and earn zero. Presence means a valid, nonempty answer; a selected option or numeric zero counts as answered.

Numeric boundaries explicitly represent greater-than, greater-than-or-equal, less-than, and less-than-or-equal. Selection supports any-of/all-of against a nonempty configured option set. Use nonnegative whole-number points initially, with checked accumulation and bounded configured values to prevent overflow. Negative points, weights, percentage normalization, and score caps are outside the initial scope.

Submission snapshots retain applicability, each question's awarded points, contributing rule identifiers and awarded values, category totals, total, definition version, and scoring semantics version. First-match traces distinguish a matching rule from a rule that actually contributes. Historical explanation uses the snapshot and immutable definition; it does not recompute under newer engine behavior.

## 8. Respondent journey and saved work

The journey is start/resume, complete categories, review answers, submit, and receive permitted results. Display application title, clear instructions, category progress, required/optional labels, help text, and a visible save state. Category navigation is accessible and adapts to small screens.

Persist each response in SQL Server against a published version. Incomplete answers can be saved; required validation must not prevent saving progress. Structurally invalid values, unknown question/option IDs, and excessive input sizes are rejected with field-level feedback. Save and exit leaves the page only after a confirmed save; on failure it stays open and explains the unsaved state.

Use optimistic concurrency for response saves. Two tabs cannot silently overwrite each other. Save requests carry a revision, return the new revision, and reject stale updates. The UI serializes local saves and preserves typed values when a conflict occurs. Browser reload or device failure before a save succeeds cannot be represented as durable progress.

Review displays visible questions and answers grouped by category, with edit links and explicit unanswered optional fields. No live score is shown to respondents. Final submission revalidates visibility, structure, required answers, access, and availability on the server.

Each response may produce at most one submission. Multiple distinct responses per person remain allowed. Use a persistent submission request identity and database uniqueness so concurrent/repeated submission of the same response returns the same result. Freeze answers, score snapshots, and response status in one transaction. Saves to submitted responses are rejected.

Database-level serialization or equivalent transactional coordination must prevent closure races: if closure commits before submission is accepted, reject submission; if submission commits first, its receipt remains valid. Replaying an already successful submission returns its receipt even after closure.

Signed-in respondents have a My applications view for their in-progress responses. Email sessions resume the response named by their grant. Full respondent history and post-submission editing are not first-release features.

## 9. Identity and email access

Access is configured per published application version. Host sign-in is the default. Use the host's stable, issuer-qualified subject identifier rather than an email address as the ownership key.

Account-free access verifies email using single-use, expiring links and then establishes a response-scoped session. The host implements an email delivery contract; the runtime owns token creation, validation, response grants, and revocation. Starting an email-mode response requires successful verification; return requests issue fresh links without revealing whether an address has saved work.

Store token digests rather than raw access tokens. Consume tokens atomically; bind the resulting session to the permitted response. A configurable default link lifetime of 30 minutes is proposed. Rate-limit issuance and redemption, avoid sensitive token/answer logging, prevent open redirects, and apply antiforgery protection to cookie-authenticated writes. Use the host's data-protection configuration for session protection across restarts or instances.

Every response read, save, resume, and submit operation verifies ownership. A guessed response identifier is insufficient. Email delivery failures leave existing saved work intact and provide a retry path. Publishing an email-mode application is blocked when the host has not configured email delivery.

Email access is account-free, not anonymous. Truly anonymous recoverable responses are deferred; the editor must not label email or signed-in responses anonymous.

## 10. Availability and results

Maintain two operational controls: accept new responses and accept submissions. Stopping new responses leaves existing responses resumable and submittable if submissions remain open. Closing submissions also stops new responses, but existing owners may resume, inspect, and save draft work; a clear closed notice disables final submission. Reopening submissions does not automatically reopen new starts; controls remain explicit.

Result visibility is versioned: confirmation only, total only, or total plus category totals. The server exposes only the permitted fields in respondent output; it never embeds forbidden details in HTML or JSON. Authorized reports retain full details. Confirmation instructions and submit text are configurable plain text.

## 11. Inspection and analysis

Provide four first-release views:

1. Overview: submitted volume over time and score distributions, filtered by application, version, and submitted date.
2. Submission list: stable pagination, submitted timestamp, submission reference, and total; search by reference and permitted respondent identity fields.
3. Submission detail: definition version, visible answers, unanswered optional questions, not-applicable questions, scoring explanation, and all totals.
4. Export: filtered CSV with stable identifiers, readable question labels, version information, answer values, and scores.

Reports read immutable submissions. In-progress responses are excluded from submitted counts and score distributions. Score distributions are separated by version by default; counts can span versions with explicit labels. Do not imply scores from different versions are equivalent.

Answer-level exports select one application/version to keep columns unambiguous. Cross-version summary exports include the version on every row. Exports enforce the same authorization and filters as screens and neutralize spreadsheet formula injection. Stream bounded projections instead of loading complete answer graphs into memory.

Persist UTC timestamps and use one documented host display timezone with explicit date-filter boundaries. EF query projections, stable pagination, cancellation, and relevant indexes are the starting point. Choose additional indexes from measured query shapes and SQL Server plans, not presumed bottlenecks. There is no arbitrary dashboard/query builder in this release.

## 12. Quality and acceptance

Use the existing professional visual direction as a starting point. All three surfaces require WCAG 2.1 AA, keyboard operation, labels, visible focus, linked error summaries, announced save/status messages, and reflow at 320 CSS pixels. Validate real screens; tokens alone do not establish compliance.

The complete reference journey must demonstrate:

- An administrator builds a synthetic five-category RFA with all initial input types, static content, conditions, and both scoring modes, previews it, and publishes.
- A respondent saves, leaves, resumes, changes a branching answer, reviews, and submits; hidden answers are excluded and the configured results appear.
- A second publication during that response does not alter its pinned definition.
- An analyst locates the submission, explains its score from stored evidence, and exports equivalent authorized data.
- Sign-in and email modes each work end to end; cross-user access and expired/reused links fail.
- Concurrent editing/saving, duplicate submission, availability races, and save/email failures behave as specified.
- A client using built packages reproduces the journey with a custom route prefix, host layout, and permission mapping.

Tests cover rule and boundary semantics, visibility dependencies, publish validation, snapshots, HTTP authorization, and browser journeys. Real SQL Server tests cover migrations, foreign keys, rowversion conflicts, unique constraints, atomic submission, idempotency, and concurrent closure/publication. Tests requiring SQL Server must report skipped when not run, not silently pass. Historical verification logs do not establish the state of the new implementation.

Use unit tests for pure behavior and real relational tests for guarantees that InMemory cannot prove. Verify package assets and all shipped target frameworks. Establish representative reporting data volumes and measured baselines during planning; do not invent latency or capacity guarantees.

## 13. Delivery stages and existing-code assessment

All stages below belong to the agreed first release, with separate detailed implementation plans as needed:

1. Establish a reproducible build/test baseline and assess existing invariants. Deliver a thin configure-preview-publish-fill-submit-report journey in the central host.
2. Complete durable progress, concurrency, retry-safe submission, operational availability, and both respondent access methods.
3. Complete the authoring workspace, conditional behavior, scoring modes/explanation, result options, and analysis/export views.
4. Verify accessible browser journeys, real SQL Server races, packaged consumption, deployment instructions, and production host integration.

These are delivery increments, not permission to call the product complete after stage 1. Implement foundational condition/snapshot/access contracts early where later stages depend on them.

Reuse existing domain layering, EF infrastructure, scoring tests, and draft/publication mechanisms only after validating their behavior. The inspected source contains editor and core services, but no runtime or reporting UI projects. Existing implementation specifically requires changes for unconditional additive scoring, inclusive-only numeric ranges, independent-category validation bypass, durable responses, rule traces, and operational controls.

No production code has been changed or tested as part of this design exercise. The implementation plan must map retained/reworked/replaced components and define integration tests before claiming completeness.

## 14. Exclusions and handoff

First release excludes multi-organization tenancy within one database, approval/reviewer workflows, payments, custom scripts, uploads, external field verification, localization, anonymous recovery, post-submission editing, per-person submission limits, scheduled deadlines, and emailed result reports. Recovery emails are included. Future features must extend the common packages rather than fork behavior into the central host.

After the owner reviews this written specification, create the implementation plan with exact project boundaries, migrations, dependency order, validation commands, package checks, and acceptance milestones. Select the execution method at that handoff. Approval of this specification is not a claim that the application has already been implemented.

# QuestionAppBuilder Platform Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Deliver the approved questionnaire platform through shared NuGet libraries and a central host, with reliable authoring, save/resume, submission, and analysis.

**Architecture:** Retain the existing layers, revise their contracts where the approved behavior requires it, and add Core and Reports Razor Class Libraries. The host owns identity, email, branding, and deployment. No microservices or shared-database tenancy.

**Tech Stack:** Verified baseline net8.0, EF Core 8, SQL Server, ASP.NET Core MVC/RCL, xUnit 2.9.3 and FluentAssertions 7. New tools and version support must be checked against primary documentation when introduced.

**Spec:** [Approved platform design](../specs/2026-10-17-questionappbuilder-platform-design.md).

**Status:** Written for owner review. No product implementation authorized through this plan yet; choose the execution method after review.

## Global Constraints

- One organization and one SQL Server database per deployment, with many configured applications and users.
- Immutable published definitions and submissions; responses pin their definition version.
- Durable save-and-resume in the first release.
- Per-application access mode: host sign-in or account-free email-link access.
- Reporting covers inspection and analysis, not reviewer assignment or approval workflows.
- Preserve unrelated user edits. Current .gitignore changes, tool directories, PRODUCT.md, DESIGN.md, docs/design, and the earlier editor plan predate this task.
- All 14 tasks below are first-release scope. A passing intermediate demonstration is not completion.
- Schema changes flow through EF migrations; do not reset any existing database. Disposable SQL tests own uniquely named databases and clean up only their own catalogs.
- Existing public API changes are intentional pre-release changes, not silently compatible updates. Record signature/route changes in integration documentation. Future released versions establish a supported compatibility surface.

## Review Focus

1. Lost responses and duplicate submissions: R2/R3 and U1 exercise revision and retry behavior.
2. Authorization through alternative routes or guessed IDs: R2/R4, U3 and A1/A2 exercise ownership and controller policies.
3. Hidden answers affecting downstream conditions or scores: E3, R3 and U1 exercise applicability and snapshots.
4. Review/publish races and warning acknowledgment reuse: E4 and U2 bind all decisions to draft revision.
5. False verification confidence: E1 makes SQL skips explicit; A3 verifies actual packages, browsers and SQL rather than only project references.

## Baseline evidence collected during planning

On 2026-10-17, the normal `dotnet test QuestionAppBuilder.sln --verbosity minimal` command could not finish because the running QuestionAppBuilder.SampleHost process (PID 59624) locked output assemblies (MSB3027/MSB3021). No user process was stopped.

The same solution succeeded using isolated build artifacts:

```powershell
$qabBaseline = Join-Path $env:TEMP ('qab-plan-baseline-' + [Guid]::NewGuid().ToString('N'))
dotnet test QuestionAppBuilder.sln --artifacts-path $qabBaseline --verbosity minimal
```

Observed: Application 43 passed; Editor 37 passed; Infrastructure 8 reported passed. QAB_TEST_SQLSERVER was absent, and inspection confirmed four SQL tests early-returned. Therefore 84 tests executed behavior and four reported success without SQL verification. No real SQL Server, browser accessibility, or new feature tests were executed during this planning turn.

The isolated artifacts used C:/Users/nchinnam/AppData/Local/Temp/qab-plan-baseline-32e83d62de824eaf8d1b3ff90593e3fb. Ordinary builds can continue to collide with the user's running app; execute in an isolated worktree or isolated output paths instead of stopping it without need.

## Retain, revise, add

| Area | Decision and evidence |
| --- | --- |
| Domain/Application/Infrastructure projects | Retain layering; extend the model through migrations |
| ScoringEngine | Retain pure-engine pattern; replace additive-only/inclusive-only behavior with explicit modes, visibility and traces |
| ApplicationDesignService | Split cloning/validation responsibilities; replace unversioned mutation signatures with revision-aware contracts |
| ApplicationRepository | Keep tracked write paths; add projected/no-tracking reads and inspect graph query shape before performance changes |
| SubmissionService | Replace direct unowned SubmitRequest flow with owned response-based atomic submission |
| Editor controllers and views | Keep RCL hosting mechanism; redesign workspace and migrate endpoints to concurrency-aware services |
| SampleHost | Keep project location; evolve into the central consuming application with production host adapters |
| Existing tests | Retain useful regressions; revise obsolete independent-category/additive-only expectations intentionally |
| Runtime and reporting | Add separate Core and Reports RCL projects, HTTP tests, and shared browser journeys |
| SQL smoke tests | Fix misleading pass behavior; expand real SQL coverage for concurrency and transactions |

## Coordinated subsystem plans

| Plan | Tasks | Testable deliverable |
| --- | --- | --- |
| [1. Definition and evaluation engine](2026-10-17-platform-01-engine.md) | E1–E4 | Validated immutable definitions, explainable evaluation, revision-safe edits/publication |
| [2. Durable responses and access](2026-10-17-platform-02-responses.md) | R1–R4 | Owned saved work, atomic submissions, availability, secure email recovery |
| [3. Runtime and authoring experiences](2026-10-17-platform-03-experiences.md) | U1–U3 | Accessible respondent flow, focused editor, real host integration |
| [4. Analysis and release](2026-10-17-platform-04-analysis-release.md) | A1–A3 | Reports/export and demonstrated installed-package release |

Default order: E1 → E2 → E3 → E4 → R1 → R2 → R3 → R4 → U1 → U2 → U3 → A1 → A2 → A3.

For an earlier end-to-end demonstration, implement U3's host identity/policy wiring after R4, then U1 and A1 before U2. Do not declare U3 complete until all its checks and integrations exist. This is an allowed sequencing adjustment, not a different product scope. The default order is simpler for a single implementer.

The dependency chain is intentional: durable ownership and idempotency are prerequisites for a trustworthy respondent UI. Do not add temporary controller-specific submission or scoring implementations to make a demo appear complete.

## Shared interface conventions

- All service async methods accept CancellationToken; times are UTC and obtained through injected TimeProvider where behavior depends on time.
- Application-level contracts live in Application, and Infrastructure implementations may reference them. Domain entities must not reference Application DTOs or EF types.
- Add Domain/Exceptions/DraftConflictException.cs, ResponseConflictException.cs and InvalidAccessLinkException.cs in E4, R2 and R4 respectively. Conflicts map to HTTP 409, semantic validation to 422, unavailable operation to 409 with a stable reason code, missing or non-owned response to 404, unauthenticated host access to challenge, and authenticated policy failure to 403. Never convert all failures to 200.
- E4 creates Application/Authoring/ICurrentActor.cs: `string GetRequiredActorKey()`. Host adapters supply a trusted actor for authoring/availability audit. Do not reuse respondent identity to authorize designers.
- R2 ResponseSnapshot explicitly exposes `Guid Id`, `int ApplicationId`, `int ApplicationVersionId`, pinned graph, answers, status, revision, updated time, and availability. The ResponseSummary return of ListMineAsync contains summary fields only.
- U1 QuestionnaireRenderModel contains ordered visible categories, typed questions/options, answer values, applicability, validation messages and navigation state. It excludes scoring rules/trace except through a separate authorized editor preview result. Internal server definition graphs must never be serialized directly to respondents.
- Revision bytes travel as Base64 with size validation. New logical keys are created server-side; accept client references only to elements owned by the target draft. New unsaved elements use temporary client keys mapped to server keys in the save result.
- E2 persists option predicates via a dedicated VisibilityPredicateOption entity/configuration (add those files); do not store uncontrolled comma-delimited IDs. E4 validates same-version membership and remaps references during cloning.
- E2's removal of IsIndependent includes adapting current service/controller/view/test callers to version-level navigation in that same commit. Do not leave the solution broken until E4. Existing tests that expected required-validation bypass must be rewritten to the approved semantics, not deleted to obtain a green run.
- E4's email-readiness check depends on an application-level host capability contract, `IHostCapabilities.CanSendResponseEmail`. Introduce that interface with E4; its default is false and R4/U3 supply the configured capability. Do not create a forward dependency on an unimplemented email sender type.
- E3's evaluation result includes visible category IDs as well as question IDs so static-only categories can render correctly. U1 creates Rendering/QuestionnaireRenderModel.cs with category, question and navigation DTOs; no Domain entity is an HTTP response model.
- Existing development definitions preserve additive/inclusive scoring. New numeric questions default to FirstMatch. Never migrate historical submissions by recalculation.
- R2 saves are full snapshots, including temporarily hidden answers. The UI retains these values until final submission; omissions mean deliberate clearing. Unknown questions/options are rejected. Once submitted, hidden values are cleared atomically from the saved draft and no longer available through runtime reads.
- New-start email grant consumption must be idempotently bound to one ResponseDraft, with a unique token/grant-to-response association added in R1/R4. A response-scoped grant cannot enumerate My applications or access another response even when email matches.
- R4 adds a grant identifier to RespondentIdentity for new-start email grants. StartAsync verifies that the granted version belongs to the requested application and uses the unique grant association on retry; it does not trust a posted version or app ID alone. Add an access test proving a start grant for app A cannot start app B.
- U2 adds ApplicationTypesController.cs, ApplicationTypeFormViewModel.cs and a corresponding application lookup service/repository method for administrator-defined types. Prevent removal of referenced types; classification remains behavior-free.
- No raw score/rule fields in respondent payloads. Runtime view models are projections; confirmation-only receipts use a separate serialized shape with no score properties, rather than null-valued score properties. The internal SubmissionReceipt may use nulls for type safety.

## Verification commands and checkpoints

Focused commands are listed in each task. From an isolated checkout, normal baseline commands are:

```powershell
dotnet build QuestionAppBuilder.sln --configuration Release
dotnet test QuestionAppBuilder.sln --configuration Release --no-build --verbosity minimal
```

For each generated migration, with a locally pinned EF tool matching the project major version:

```powershell
dotnet ef migrations add <TaskMigrationName> --project src/QuestionAppBuilder.Infrastructure --startup-project src/QuestionAppBuilder.Infrastructure
dotnet ef migrations script --idempotent --project src/QuestionAppBuilder.Infrastructure --startup-project src/QuestionAppBuilder.Infrastructure --output <isolated-artifact-directory>/migration.sql
```

Inspect FK delete behavior, uniqueness, backfills, locking and table/index operations; apply through the SQL fixture to a clean database and to a database at the previous migration. Test fixtures build their own catalogs from QAB_TEST_SQLSERVER and never apply migrations to the sample application's existing database. No hand-authored DDL replaces EF migrations. Parameterized SQL used for transactional application locking is operational code, not schema deployment.

Pin any local EF tool manifest in E2 if none exists. Use exact resolved versions compatible with the current SDK; record them. Browser and packaging test setup belongs in U1/A3, including explicit readiness waits and isolated host ports/databases. CI release checks must fail when required SQL/browser prerequisites are missing; ordinary developer runs may visibly skip SQL tests.

Each task follows failing test → implementation → focused passing tests → affected regression tests → scoped commit. The final release additionally runs real SQL races, installed-package journeys, browser tests, and accessibility checks. Do not create tests for documentation-only edits or count unexecuted tests as proof.

## Spec coverage and self-review

| Specification section | Owning tasks |
| --- | --- |
| 1–3 purpose, delivery, architecture | E1, U3, A3 |
| 4 definitions and publication | E2, E4 |
| 5 editor | E4, U2 |
| 6 conditions/navigation | E2, E3, U1 |
| 7 scoring/explanations | E3, R1, R3, U2, A1 |
| 8 durable respondent journey | R1–R3, U1 |
| 9 identity/email | R4, U3 |
| 10 availability/results | E2, R3, U1, U2 |
| 11 reporting/export | A1, A2 |
| 12 quality/acceptance | E1, all task tests, A3 |
| 13 delivery and assessment | this roadmap, all four plans |
| 14 exclusions and handoff | retained unchanged; no excluded features added |

Self-review addressed cross-plan DTO naming, exception locations, raw definition leakage into runtime JSON, ambiguous access-grant ownership, hidden-value clearing, honest SQL skips, and the sample host's locked outputs. Remaining implementation choices are explicitly bounded by task contracts; performance conclusions require measured SQL plans rather than guesses.

## Execution handoff

- [x] Record the approved specification.
- [x] Inspect current contracts and run an isolated baseline.
- [x] Write and self-review the coordinated implementation plans.
- [ ] Owner reviews this plan and chooses the execution method.
- [ ] Execute tasks and record real verification evidence.

Recommended: native execution in this session, with one independent review at the end. The tasks share evolving definition/response contracts, making sequential ownership useful. Subagent-driven execution is also available, with an implementer and reviewer for each task, at greater coordination/context cost. Neither method has been selected yet.

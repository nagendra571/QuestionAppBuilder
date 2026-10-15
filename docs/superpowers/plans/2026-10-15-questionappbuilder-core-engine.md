# QuestionAppBuilder Core Engine Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship the QuestionAppBuilder core engine — versioned application definitions persisted with EF Core, a rule-based scoring engine, and services to design, publish, and submit scored questionnaires — fully covered by automated tests.

**Architecture:** Layered projects (Domain → Application → Infrastructure) on .NET 8 and EF Core 8 with SQL Server. Definitions are versioned; published versions are immutable, and further edits clone a new draft. A fill pins a published version; submissions snapshot per-question, per-category, and total scores. Unit/integration tests run on EF InMemory; an env-gated smoke suite verifies the SQL Server–only behaviors InMemory fakes (unique indexes, rowversion, string-enum storage).

**Tech Stack:** .NET 8, EF Core 8 (`Microsoft.EntityFrameworkCore.SqlServer`, `Microsoft.EntityFrameworkCore.InMemory` for tests), xUnit, FluentAssertions 7.x (8.x is commercially licensed — stay on 7.x), SQL Server.

**Spec:** `docs/superpowers/specs/2026-10-15-questionappbuilder-design.md`

## Global Constraints

- TFM: `net8.0` only. `<Nullable>enable</Nullable>`, `<ImplicitUsings>enable</ImplicitUsings>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- SQL Server only; `Microsoft.EntityFrameworkCore.SqlServer` version `8.0.*` (record the resolved patch in the Task 1 commit message).
- Length limits live in `QuestionAppBuilder.Domain.ValidationLimits`: Application.Name 200; Element.Label 500; Element.HelpText 2000; QuestionOption.Label 200; QuestionOption.Value 100; answer text 4000; ApplicationType.Name 100.
- `int` identity keys; UTC `DateTime` timestamps; enums persist as strings.
- Table names (plural): Applications, ApplicationTypes, ApplicationVersions, Categories, Elements, Questions, QuestionOptions, ScoringRules, ScoringRuleOptions, Submissions, Answers, AnswerSelectedOptions, SubmissionCategoryScores.
- Numeric-tier bounds are inclusive on both ends (Min ≤ value ≤ Max).
- Public types are a package compatibility surface: additive changes only within a major version. No TemplateBuilder code references. English-only strings.
- Tests: xUnit + FluentAssertions. Persistence tests use EF InMemory — its divergences from SQL Server (unique indexes, rowversion, enum-as-string storage) are covered by Task 7's env-gated SQL suite; no test may claim InMemory proves those.
- One commit per task, conventional-commit messages.

## Review Focus

- **Tier boundaries:** a rule with Max=25 fires at exactly 25, Min=10 at exactly 10; an off-by-one silently mis-scores. Pinned in Task 3.
- **Misplaced answers:** an option id belonging to another question, or text on a numeric question, must be rejected — never silently scored. Pinned in Task 5.
- **Over-length content:** a 4001-char answer or 501-char label fails with a domain exception before SQL Server sees it. Pinned in Tasks 2, 4, 5.
- **Zero-answer submissions:** still persist, score 0 for every question, and store a zero row per category. Pinned in Task 5.
- **Clone isolation:** editing after publish never mutates published rows. Pinned in Task 4.

---

## File Structure

```
QuestionAppBuilder.sln
Directory.Build.props            build-wide TFM/analyzer settings
.gitignore
src/QuestionAppBuilder.Domain/
  ValidationLimits.cs            the length constants above
  Enums/AnswerType.cs            Textbox, Textarea, Dropdown, RadioGroup, Checkboxes, Number
  Enums/ElementKind.cs           Question, Content
  Enums/RuleFamily.cs            Presence, Selection, NumericRange
  Enums/MembershipOperator.cs    AnyOf, AllOf
  Enums/VersionStatus.cs         Draft, Published
  Entities/                      one file per entity (shapes in Task 2)
  Graphs/ApplicationVersionGraph.cs   Version + Categories with child navigation
  Interfaces/IApplicationRepository.cs
  Interfaces/ISubmissionRepository.cs
  Exceptions/NotFoundException.cs
  Exceptions/DuplicateApplicationNameException.cs
  Exceptions/VersionImmutableException.cs
  Exceptions/AnswerValidationException.cs
  Exceptions/DefinitionValidationException.cs
src/QuestionAppBuilder.Application/
  Scoring/IScoringEngine.cs
  Scoring/ScoringEngine.cs
  Scoring/AnswerValue.cs         record: Text?, Number?, SelectedOptionIds?
  Scoring/SubmissionScore.cs     records: SubmissionScore, QuestionScore, CategoryScore
  Services/IApplicationDesignService.cs
  Services/ApplicationDesignService.cs
  Services/ISubmissionService.cs
  Services/SubmissionService.cs
  Services/SubmitRequest.cs
  Options/QuestionAppBuilderOptions.cs
src/QuestionAppBuilder.Infrastructure/
  Persistence/QuestionAppBuilderDbContext.cs
  Persistence/QuestionAppBuilderDbContextFactory.cs   design-time factory
  Persistence/Configurations/    one IEntityTypeConfiguration per entity
  Persistence/Migrations/        generated by Task 2
  Repositories/ApplicationRepository.cs
  Repositories/SubmissionRepository.cs
  DependencyInjection/ServiceCollectionExtensions.cs   AddQuestionAppBuilder
tests/QuestionAppBuilder.Application.Tests/
  SolutionTests.cs               Task 1 sanity
  Scoring/ScoringEngineTests.cs
  Services/ApplicationDesignServiceTests.cs
  Services/SubmissionServiceTests.cs
tests/QuestionAppBuilder.Infrastructure.Tests/
  Persistence/ModelRoundTripTests.cs
  DependencyInjection/ServiceCollectionExtensionsTests.cs
  SqlServer/SqlServerSmokeTests.cs
```

---

### Task 1: Solution skeleton and build infrastructure

**Files:**
- Create: `QuestionAppBuilder.sln`, `Directory.Build.props`, `.gitignore`
- Create: `src/QuestionAppBuilder.Domain/`, `src/QuestionAppBuilder.Application/`, `src/QuestionAppBuilder.Infrastructure/`
- Create: `tests/QuestionAppBuilder.Application.Tests/`, `tests/QuestionAppBuilder.Infrastructure.Tests/`

**Interfaces:**
- Consumes: nothing.
- Produces: a repository containing five buildable projects wired into one solution; all later tasks add files inside this layout.

- [ ] **Step 1: Init the repository**

```powershell
git init -b main
dotnet new gitignore
```

- [ ] **Step 2: Create solution and projects**

```powershell
dotnet new sln -n QuestionAppBuilder
dotnet new classlib -n QuestionAppBuilder.Domain -o src/QuestionAppBuilder.Domain
dotnet new classlib -n QuestionAppBuilder.Application -o src/QuestionAppBuilder.Application
dotnet new classlib -n QuestionAppBuilder.Infrastructure -o src/QuestionAppBuilder.Infrastructure
dotnet new xunit -n QuestionAppBuilder.Application.Tests -o tests/QuestionAppBuilder.Application.Tests
dotnet new xunit -n QuestionAppBuilder.Infrastructure.Tests -o tests/QuestionAppBuilder.Infrastructure.Tests
dotnet sln add (Get-ChildItem src,tests -Filter *.csproj -Recurse).FullName
```

- [ ] **Step 3: Wire references and packages**

```powershell
dotnet add src/QuestionAppBuilder.Application reference src/QuestionAppBuilder.Domain
dotnet add src/QuestionAppBuilder.Infrastructure reference src/QuestionAppBuilder.Application
dotnet add tests/QuestionAppBuilder.Application.Tests reference src/QuestionAppBuilder.Application src/QuestionAppBuilder.Infrastructure
dotnet add tests/QuestionAppBuilder.Infrastructure.Tests reference src/QuestionAppBuilder.Infrastructure
dotnet add src/QuestionAppBuilder.Infrastructure package Microsoft.EntityFrameworkCore.SqlServer --version 8.0.*
dotnet add src/QuestionAppBuilder.Infrastructure package Microsoft.EntityFrameworkCore.Design --version 8.0.*
dotnet add tests/QuestionAppBuilder.Infrastructure.Tests package Microsoft.EntityFrameworkCore.InMemory --version 8.0.*
dotnet add tests/QuestionAppBuilder.Application.Tests package FluentAssertions --version 7.*
dotnet add tests/QuestionAppBuilder.Infrastructure.Tests package FluentAssertions --version 7.*
```

`Microsoft.EntityFrameworkCore.Design` gets `PrivateAssets="all"`. Delete the template `Class1.cs` / `UnitTest1.cs` files.

- [ ] **Step 4: Add `Directory.Build.props`**

```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>latest</LangVersion>
  </PropertyGroup>
</Project>
```

Remove the now-duplicate `<TargetFramework>` properties from each csproj.

- [ ] **Step 5: Write the sanity test** — `tests/QuestionAppBuilder.Application.Tests/SolutionTests.cs`

```csharp
namespace QuestionAppBuilder.Application.Tests;

public class SolutionTests
{
    [Fact]
    public void Solution_builds_and_tests_run()
    {
        (1 + 1).Should().Be(2);
    }
}
```

(Add `using FluentAssertions;` as needed.)

- [ ] **Step 6: Verify**

Run: `dotnet build` then `dotnet test`. Expected: build succeeds with zero warnings; 1 test passes.

- [ ] **Step 7: Commit**

```powershell
git add -A
git commit -m "chore: scaffold solution, projects, and test infrastructure (EF Core 8.0.x)"
```

---

### Task 2: Domain model, EF Core mapping, initial migration

**Files:**
- Create: all files under `src/QuestionAppBuilder.Domain/` (per File Structure)
- Create: `src/QuestionAppBuilder.Infrastructure/Persistence/` (DbContext, factory, configurations)
- Create: `src/QuestionAppBuilder.Infrastructure/Persistence/Migrations/` (generated)
- Test: `tests/QuestionAppBuilder.Infrastructure.Tests/Persistence/ModelRoundTripTests.cs`

**Interfaces:**
- Consumes: Task 1 layout.
- Produces (exact shapes — every later task builds on these):

```csharp
// Domain.Enums
public enum AnswerType { Textbox, Textarea, Dropdown, RadioGroup, Checkboxes, Number }
public enum ElementKind { Question, Content }
public enum RuleFamily { Presence, Selection, NumericRange }
public enum MembershipOperator { AnyOf, AllOf }
public enum VersionStatus { Draft, Published }

// Domain.Entities (public get/set on every property; EF-friendly)
public class ApplicationType      { int Id; string Name; int SortOrder; }
public class Application          { int Id; string Name; int ApplicationTypeId; int? CurrentVersionId;
                                    byte[] RowVersion; DateTime CreatedUtc; DateTime UpdatedUtc;
                                    ApplicationType ApplicationType; ApplicationVersion? CurrentVersion; }
public class ApplicationVersion   { int Id; int ApplicationId; int VersionNumber; VersionStatus Status;
                                    DateTime CreatedUtc; DateTime? PublishedUtc; List<Category> Categories; }
public class Category             { int Id; int ApplicationVersionId; string Name; int SortOrder; bool IsIndependent;
                                    List<Element> Elements; }
public class Element              { int Id; int CategoryId; ElementKind Kind; string Label; string? HelpText; int SortOrder;
                                    Question? Question; }
public class Question             { int Id; int ElementId; AnswerType AnswerType; bool IsRequired;
                                    List<QuestionOption> Options; List<ScoringRule> Rules; }
public class QuestionOption       { int Id; int QuestionId; string Label; string Value; int SortOrder; }
public class ScoringRule          { int Id; int QuestionId; RuleFamily Family; int Points;
                                    MembershipOperator? MembershipOperator; decimal? MinValue; decimal? MaxValue;
                                    List<ScoringRuleOption> Options; }
public class ScoringRuleOption    { int Id; int ScoringRuleId; int QuestionOptionId; }
public class Submission           { int Id; int ApplicationId; int ApplicationVersionId; string? UserId;
                                    DateTime SubmittedUtc; int TotalScore;
                                    List<Answer> Answers; List<SubmissionCategoryScore> CategoryScores; }
public class Answer               { int Id; int SubmissionId; int QuestionId; string? TextValue; decimal? NumberValue;
                                    int PointsEarned; List<AnswerSelectedOption> SelectedOptions; }
public class AnswerSelectedOption { int Id; int AnswerId; int QuestionOptionId; }
public class SubmissionCategoryScore { int Id; int SubmissionId; int CategoryId; int Score; }

// Domain.Graphs
public class ApplicationVersionGraph
{
    public required ApplicationVersion Version { get; init; }
    public required IReadOnlyList<Category> Categories { get; init; }
}
```

- Produces (repositories — Domain.Interfaces):

```csharp
public interface IApplicationRepository
{
    Task<Application?> GetAsync(int applicationId, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);
    Task<IReadOnlyList<Application>> ListAsync(CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationType>> ListApplicationTypesAsync(CancellationToken ct = default);
    Task<ApplicationVersionGraph?> GetDraftGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> GetVersionGraphAsync(int applicationVersionId, CancellationToken ct = default);
    void Add(Application application);
    void AddGraph(ApplicationVersionGraph graph);
    Task SaveChangesAsync(CancellationToken ct = default);
}

public interface ISubmissionRepository
{
    Task AddAsync(Submission submission, CancellationToken ct = default);
    Task SaveChangesAsync(CancellationToken ct = default);
}
```

- Produces (exceptions): `NotFoundException(string message)`, `DuplicateApplicationNameException(string name)`, `VersionImmutableException(string message)`, `AnswerValidationException(string message, IReadOnlyList<int> questionIds)` with `QuestionIds` property, `DefinitionValidationException(string message)` — all derive `Exception`.

- [ ] **Step 1: Write the failing tests** — `ModelRoundTripTests`

Two facts:
1. `Model_enforces_length_limits` — build the model via `new QuestionAppBuilderDbContext(options).Model`, then assert metadata: `FindEntityType(typeof(Domain.Entities.Application))!.FindProperty(nameof(Domain.Entities.Application.Name))!.GetMaxLength()` is 200, `ApplicationType.Name` is 100, `Element.Label` is 500, `Element.HelpText` is 2000, `QuestionOption.Label` is 200, `QuestionOption.Value` is 100.
2. `Graph_round_trips` — InMemory database; build a full graph (application → version → category → question element with 2 options + 2 rules + 1 content element), `SaveChanges`, open a new context, reload the graph, and assert: counts, `ScoringRule.MinValue` decimal preserved, `QuestionOption.Value` preserved, `Category.IsIndependent` preserved.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/QuestionAppBuilder.Infrastructure.Tests` — expected: compile fails (no types yet).

- [ ] **Step 3: Implement the domain types and EF mapping**

- All entities/enums/exceptions/repository interfaces per the shapes above; `ValidationLimits` constants (`NameMaxLength = 200`, `LabelMaxLength = 500`, `HelpTextMaxLength = 2000`, `OptionLabelMaxLength = 200`, `OptionValueMaxLength = 100`, `AnswerTextMaxLength = 4000`, `ApplicationTypeNameMaxLength = 100`).
- `QuestionAppBuilderDbContext : DbContext` with `DbSet<>` per entity; apply configurations from the assembly.
- Configurations: one `IEntityTypeConfiguration<>` per entity, in `Persistence/Configurations/`. Required decisions:
  - All tables named per Global Constraints.
  - `Application.Name`: required, max 200, unique index (`Applications.Name`).
  - `ApplicationVersion`: unique index on (`ApplicationId`, `VersionNumber`).
  - `Application.RowVersion`: `.IsRowVersion()`. Declare `byte[] RowVersion` initialized to `Array.Empty<byte>()` so NRT analysis stays clean — EF overwrites it on read/write.
  - All enums: `.HasConversion<string>()` with max length 32.
  - `ScoringRule`: `MinValue`/`MaxValue` as `decimal(18,4)`; `MembershipOperator` nullable string.
  - `Answer`: `NumberValue` decimal(18,4), `TextValue` max 4000.
  - `AnswerSelectedOption`, `SubmissionCategoryScore`, `ScoringRuleOption`: composite unique indexes on their FKs (`(AnswerId, QuestionOptionId)`, `(SubmissionId, CategoryId)`, `(ScoringRuleId, QuestionOptionId)`).
  - `ApplicationType` seed via `HasData`: `(1, "Request For Application (RFA)", 1)`, `(2, "Request For Information (RFI)", 2)`.
  - Cascade behavior: `Category`/`Element`/`Question`/`QuestionOption`/`ScoringRule` chain cascades from `ApplicationVersion`; `Submission` restricts (no cascade delete from versions — history must survive); `Answer` cascades from `Submission`.
- `QuestionAppBuilderDbContextFactory : IDesignTimeDbContextFactory<QuestionAppBuilderDbContext>` using a placeholder connection string (`Server=(localdb)\QuestionAppBuilderDesignTime;Database=QuestionAppBuilder`) — design-time only, never opened.

- [ ] **Step 4: Generate the initial migration**

Run: `dotnet ef migrations add InitialCreate --project src/QuestionAppBuilder.Infrastructure --startup-project src/QuestionAppBuilder.Infrastructure --output-dir Persistence/Migrations`

Inspect the generated `Up()`: all 13 tables present, two seed rows, unique indexes, rowversion column. Fix mappings (not the migration) if anything is off, then `dotnet ef migrations remove` and regenerate.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/QuestionAppBuilder.Infrastructure.Tests` — expected: PASS.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: domain model, EF Core mapping, and initial migration"
```

---

### Task 3: Scoring engine

**Files:**
- Create: `src/QuestionAppBuilder.Application/Scoring/` (all five files)
- Test: `tests/QuestionAppBuilder.Application.Tests/Scoring/ScoringEngineTests.cs`

**Interfaces:**
- Consumes: `ApplicationVersionGraph`, entities (Task 2).
- Produces (exact):

```csharp
namespace QuestionAppBuilder.Application.Scoring;

public sealed record AnswerValue(string? Text, decimal? Number, IReadOnlyCollection<int>? SelectedOptionIds);

public sealed record QuestionScore(int QuestionId, int Points);
public sealed record CategoryScore(int CategoryId, int Score);
public sealed record SubmissionScore(int Total, IReadOnlyList<QuestionScore> Questions, IReadOnlyList<CategoryScore> Categories);

public interface IScoringEngine
{
    SubmissionScore Score(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers);
}
```

**Rule semantics (locked — the engine's contract):**
- A question is *answered*: Textbox/Textarea → `Text` is non-null and non-whitespace; Number → `Number` has a value; Dropdown/RadioGroup/Checkboxes → `SelectedOptionIds` is non-empty.
- `Presence` rule fires iff the question is answered and adds `Points`.
- `Selection` rule fires iff selected set satisfies the operator against the rule's option ids: `AnyOf` → intersection non-empty; `AllOf` → every rule option id is selected. Adds `Points`.
- `NumericRange` rule fires iff `Number` has a value and (`MinValue` is null or value ≥ MinValue) and (`MaxValue` is null or value ≤ MaxValue). Bounds inclusive. Adds `Points`.
- Rules are additive; over-lapping rules all fire. Content elements are ignored.
- `Questions` contains **every** scorable question (one with a `Question` row) with its points (0 when unanswered or no rule fires). `Categories` contains every category with the sum of its questions' points. `Total` is the sum of all question points.
- Engine is pure: no `DbContext`, no repository, deterministic.

- [ ] **Step 1: Write the failing tests** — `ScoringEngineTests` builds small graphs with a local helper (`BuildGraph(...)`); one fact per behavior:

1. `Presence_fires_only_when_answered` — text "abc" → rule(+1) fires; whitespace " " → 0; null → 0.
2. `Presence_counts_zero_number_as_answered` — Number=0 with presence rule → fires.
3. `Selection_any_of_fires_on_membership` — dropdown rule AnyOf {opt1,opt2}, +5; selecting opt2 → 5; opt3 → 0.
4. `Selection_all_of_requires_every_option` — checkbox rule AllOf {opt1,opt2}, +3; both → 3; only opt1 → 0.
5. `Numeric_tiers_are_inclusive_at_both_bounds` — rules (Min=26, +2), (Min=10, Max=25, +1); values 26→2, 25→1, 10→1, 9→0.
6. `Rules_are_additive` — checkbox with per-option rules (opt1:+1, opt2:+2); both selected → 3.
7. `Overlapping_rules_all_fire` — two numeric rules both matching → sum.
8. `Content_elements_are_ignored` — content element in graph produces no question score.
9. `Unanswered_questions_score_zero_but_appear` — 2 questions, answer only one → Questions has 2 entries, the other 0.
10. `Category_totals_and_grand_total` — 2 categories, points split → per-category sums and `Total` correct.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/QuestionAppBuilder.Application.Tests` — expected: compile failure (no types).

- [ ] **Step 3: Implement `ScoringEngine`**

Walk `graph.Categories` → `Elements` (`Kind == Question`, `Question != null`) → evaluate each rule per the locked semantics. Write the body so each of the ten facts above decides one branch; no early returns that would skip recording a zero question.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/QuestionAppBuilder.Application.Tests` — expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: rule-based scoring engine with presence, selection, and numeric-tier rules"
```

---

### Task 4: Application design service (create, edit drafts, publish, clone)

**Files:**
- Create: `src/QuestionAppBuilder.Application/Services/IApplicationDesignService.cs`, `ApplicationDesignService.cs`
- Create: `src/QuestionAppBuilder.Infrastructure/Repositories/ApplicationRepository.cs`
- Test: `tests/QuestionAppBuilder.Application.Tests/Services/ApplicationDesignServiceTests.cs`

**Interfaces:**
- Consumes: repositories, entities, exceptions (Task 2); nothing from Task 3.
- Produces (exact):

```csharp
public interface IApplicationDesignService
{
    Task<IReadOnlyList<ApplicationType>> ListApplicationTypesAsync(CancellationToken ct = default);
    Task<Application> CreateApplicationAsync(string name, int applicationTypeId, CancellationToken ct = default);
    Task<IReadOnlyList<Application>> ListApplicationsAsync(CancellationToken ct = default);
    Task<ApplicationVersionGraph> GetDraftGraphAsync(int applicationId, CancellationToken ct = default);
    Task<Category> AddCategoryAsync(int applicationId, string name, bool isIndependent = false, CancellationToken ct = default);
    Task UpdateCategoryAsync(int categoryId, string name, bool isIndependent, CancellationToken ct = default);
    Task RemoveCategoryAsync(int categoryId, CancellationToken ct = default);
    Task<Element> AddContentElementAsync(int categoryId, string label, string? helpText = null, CancellationToken ct = default);
    Task<Question> AddQuestionAsync(int categoryId, string label, AnswerType answerType, bool isRequired = true, string? helpText = null, CancellationToken ct = default);
    Task<QuestionOption> AddOptionAsync(int questionId, string label, string value, CancellationToken ct = default);
    Task<ScoringRule> AddRuleAsync(int questionId, RuleFamily family, int points, MembershipOperator? membershipOperator = null, IReadOnlyCollection<int>? optionIds = null, decimal? minValue = null, decimal? maxValue = null, CancellationToken ct = default);
    Task RemoveElementAsync(int elementId, CancellationToken ct = default);
    Task RemoveOptionAsync(int optionId, CancellationToken ct = default);
    Task RemoveRuleAsync(int ruleId, CancellationToken ct = default);
    Task PublishAsync(int applicationId, CancellationToken ct = default);
}
```

**Locked behaviors:**
- `CreateApplicationAsync`: rejects duplicate names (`DuplicateApplicationNameException` via `NameExistsAsync` pre-check; the unique index in Task 2 is the backstop), creates `Version 1` in `Draft`.
- All mutation methods operate on the **current draft**; if the application has no draft (it was published), the first mutation auto-clones the current published version into `VersionNumber = max+1` as a new `Draft` (`DeepClone`), leaving published rows untouched. `GetDraftGraphAsync` returns that draft, or throws `NotFoundException`.
- `SortOrder` on new categories/elements/options = current max + 1.
- Validation before save: labels/names/values ≤ limits (`DefinitionValidationException`); a `Selection` rule needs ≥1 `optionIds` and those options must belong to the same question; a `NumericRange` rule needs Min and/or Max; `Every Selection option must exist on the question`; content elements carry no question and questions are always `ElementKind.Question`.
- `PublishAsync`: requires ≥1 category and ≥1 scorable question (`DefinitionValidationException`); sets draft `Status = Published` + `PublishedUtc`; sets `Application.CurrentVersionId`; commits. Called with no draft → `DefinitionValidationException("No draft to publish.")`.
- `ApplicationRepository`: maps the interfaces above onto `QuestionAppBuilderDbContext`; `GetDraftGraphAsync` loads the newest `Draft` version with `AsNoTracking` **off** (it will be edited); `GetVersionGraphAsync` uses `.AsNoTracking()` (read path); no lazy loading — explicit `Include` tree only.

- [ ] **Step 1: Write the failing tests** — `ApplicationDesignServiceTests` (each fact builds its own InMemory-backed service via a shared `TestContext` helper that creates the DbContext, repository, and service):

1. `CreateApplication_starts_draft_version_one` — name/type persisted; graph exists with `VersionNumber == 1`, `Status == Draft`.
2. `Duplicate_name_is_rejected` — second create with same name throws `DuplicateApplicationNameException`.
3. `Add_category_element_question_option_rule_builds_the_draft` — happy-path chain; graph reflects every addition, `SortOrder` increments.
4. `Out_of_range_label_is_rejected` — 501-char label → `DefinitionValidationException`.
5. `Selection_rule_with_option_from_another_question_is_rejected` — `DefinitionValidationException`.
6. `Publish_sets_status_and_current_version` — after publish: `Status == Published`, `PublishedUtc != null`, `application.CurrentVersionId == version.Id`.
7. `Publish_requires_a_question` — app with category but no questions → `DefinitionValidationException`.
8. `Publish_with_no_draft_throws` — publish twice → `DefinitionValidationException`.
9. `Edit_after_publish_clones_a_new_draft` (Review Focus) — publish, then `AddCategoryAsync`; assert new `VersionNumber == 2`, `Status == Draft`; re-fetch version 1 graph and assert its category count and labels are **unchanged**.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/QuestionAppBuilder.Application.Tests` — expected: compile failure.

- [ ] **Step 3: Implement `ApplicationDesignService` and `ApplicationRepository`**

Implement per the locked behaviors. Deep clone copies the graph into new entity instances (new Ids from the database) — never reuse published instances.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/QuestionAppBuilder.Application.Tests` — expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: application design service with immutable published versions"
```

---

### Task 5: Submission service (validate, score, snapshot)

**Files:**
- Create: `src/QuestionAppBuilder.Application/Services/ISubmissionService.cs`, `SubmissionService.cs`, `SubmitRequest.cs`
- Create: `src/QuestionAppBuilder.Infrastructure/Repositories/SubmissionRepository.cs`
- Test: `tests/QuestionAppBuilder.Application.Tests/Services/SubmissionServiceTests.cs`

**Interfaces:**
- Consumes: `IScoringEngine`/`AnswerValue`/`SubmissionScore` (Task 3); repositories, entities, exceptions (Task 2); the design service to build fixtures (Task 4).
- Produces (exact):

```csharp
public sealed record SubmitRequest(
    int ApplicationId,
    int ApplicationVersionId,
    IReadOnlyDictionary<int, AnswerValue> Answers,
    string? UserId = null);

public interface ISubmissionService
{
    Task<Submission> SubmitAsync(SubmitRequest request, CancellationToken ct = default);
}
```

**Locked behaviors:**
- Loads the pinned version graph via `IApplicationRepository.GetVersionGraphAsync`. If it doesn't exist, isn't published, or `Version.ApplicationId != request.ApplicationId` → `NotFoundException`.
- Per-answer validation, all failures `AnswerValidationException` with the offending question ids:
  - question id not in the graph, or the element is content → reject.
  - Values sit only in the slot their answer type owns; a value in any other slot is rejected, never ignored (misplaced values fail loudly — Review Focus):
    - Textbox/Textarea: only `Text` may be set (≤ 4000 chars); `Number` or `SelectedOptionIds` non-null → reject.
    - Number: only `Number` may be set; `Text` non-null or `SelectedOptionIds` non-null → reject.
    - Dropdown/RadioGroup/Checkboxes: only `SelectedOptionIds` may be set; `Text`/`Number` non-null → reject; every id must belong to that question; Dropdown/RadioGroup allow at most 1 selection; Checkboxes allow many.
- Required gating: for each category with `IsIndependent == false`, every required question must be *answered* (engine's answered semantics); collect all missing ids across categories and throw one `AnswerValidationException`. Independent categories are exempt; their provided answers still score.
- Persist: one `Submission` (`SubmittedUtc = DateTime.UtcNow`, `UserId` as given, `TotalScore` from the engine) + one `Answer` row **per provided answer** (`TextValue`/`NumberValue`/`SelectedOptions` as appropriate, `PointsEarned` from the engine's `QuestionScore`) + one `SubmissionCategoryScore` row **for every category** of the pinned version (0 when nothing scored). Zero answers is valid and persists a zero total.
- `SubmissionRepository.AddAsync`: `Add` the graph (answers + category scores traversed by navigation) and save; single `SaveChangesAsync` call.

- [ ] **Step 1: Write the failing tests** — `SubmissionServiceTests` (fixtures built through `IApplicationDesignService` over the same InMemory context):

1. `Submits_and_persists_scored_snapshot` — 2 categories with rules; assert persisted `TotalScore`, per-answer `PointsEarned`, and one `SubmissionCategoryScore` per category with correct sums.
2. `Unknown_question_id_is_rejected` — answer for question 9999 → `AnswerValidationException` listing it.
3. `Option_from_another_question_is_rejected` (Review Focus) — checkbox answer with the other question's option id → rejected, nothing persisted.
4. `Misplaced_values_are_rejected` (Review Focus) — `Text` set on a Number question → rejected; `SelectedOptionIds` set on a Textbox question → rejected; nothing persisted.
5. `Missing_required_in_standard_category_is_rejected` — two missing required → exception lists both ids.
6. `Independent_category_may_be_left_empty` — required question inside `IsIndependent` category left empty → submission succeeds; category score row present with 0.
7. `Zero_answers_persist_with_zero_total` (Review Focus) — all-optional app, empty answer dict → persists, `TotalScore == 0`, zero rows per category.
8. `Over_length_text_is_rejected` (Review Focus) — 4001 chars → `AnswerValidationException`, no `SqlException`.
9. `Unpublished_or_mismatched_version_is_not_found` — publish → clone draft → submit against the draft id → `NotFoundException`.
10. `Anonymous_submission_has_null_user` — `UserId = null` persists.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/QuestionAppBuilder.Application.Tests` — expected: compile failure.

- [ ] **Step 3: Implement `SubmissionService` and `SubmissionRepository`**

Validation first (throw with all offending ids), then `IScoringEngine.Score`, then build the persist graph exactly as locked above. Do not recompute scores anywhere else.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/QuestionAppBuilder.Application.Tests` — expected: PASS.

- [ ] **Step 5: Commit**

```powershell
git add -A
git commit -m "feat: submission service with validation and score snapshots"
```

---

### Task 6: DI wiring and end-to-end flow

**Files:**
- Create: `src/QuestionAppBuilder.Application/Options/QuestionAppBuilderOptions.cs`
- Create: `src/QuestionAppBuilder.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs`
- Test: `tests/QuestionAppBuilder.Infrastructure.Tests/DependencyInjection/ServiceCollectionExtensionsTests.cs`

**Interfaces:**
- Consumes: everything above.
- Produces (exact):

```csharp
public sealed class QuestionAppBuilderOptions
{
    public required string ConnectionString { get; set; }
}

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddQuestionAppBuilder(
        this IServiceCollection services,
        Action<QuestionAppBuilderOptions> configure);
}
```

- `AddQuestionAppBuilder` validates `ConnectionString` is set (`InvalidOperationException` with a clear message otherwise), registers: options (`services.AddSingleton`), `QuestionAppBuilderDbContext` via `UseSqlServer`, `IApplicationRepository`, `ISubmissionRepository`, `IScoringEngine` (singleton — pure), `IApplicationDesignService`, `ISubmissionService` (scoped).

- [ ] **Step 1: Write the failing tests** — `ServiceCollectionExtensionsTests`

1. `Missing_connection_string_throws` — `AddQuestionAppBuilder(_ => { })` → `InvalidOperationException`.
2. `End_to_end_create_publish_submit` — compose a `ServiceProvider` with `AddQuestionAppBuilder(o => o.ConnectionString = "Server=unused");` then remove the registered `DbContextOptions<QuestionAppBuilderDbContext>` and re-add the context on InMemory (this is the documented way tests override the provider); resolve `IApplicationDesignService` + `ISubmissionService`; create app → category → question → option → rule; publish; submit; assert persisted score and that a second submit on the same pinned version also works while the draft's newer version id still holds no submissions.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/QuestionAppBuilder.Infrastructure.Tests` — expected: compile failure.

- [ ] **Step 3: Implement options + extension + registration**

As locked above. Scoped services resolve their own DbContext from DI; repositories take `QuestionAppBuilderDbContext` in the constructor.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/QuestionAppBuilder.Infrastructure.Tests` — expected: PASS.

- [ ] **Step 5: Verify the whole solution**

Run: `dotnet build` and `dotnet test` (whole solution). Expected: zero warnings, all tests green.

- [ ] **Step 6: Commit**

```powershell
git add -A
git commit -m "feat: AddQuestionAppBuilder DI wiring with end-to-end flow test"
```

---

### Task 7: SQL Server smoke suite (the behaviors InMemory fakes)

**Files:**
- Create: `tests/QuestionAppBuilder.Infrastructure.Tests/SqlServer/SqlServerSmokeTests.cs`
- Create: `docs/superpowers/plans/notes/2026-10-15-sql-server-verification.md` (run log — commands + observed results, filled during this task)

**Interfaces:**
- Consumes: `QuestionAppBuilderDbContext`, Task 6 DI.
- Produces: an env-gated suite reading `QAB_TEST_SQLSERVER` (connection string). When unset, each test returns early with an explanatory comment — these tests never run against InMemory; that is the point.

- [ ] **Step 1: Write the failing/unrunnable-elsewhere tests** — `SqlServerSmokeTests`

1. `Migrations_apply_and_seed` — `Database.Migrate()` on the env connection string; assert `ApplicationTypes` contains RFA and RFI by raw read.
2. `Duplicate_application_name_violates_unique_index` — insert two `Application` rows with the same name via the context → `DbUpdateException`.
3. `RowVersion_blocks_lost_update` — two contexts load the same `Application`; both edit; first saves; second `SaveChanges` → `DbUpdateConcurrencyException`.
4. `Enums_persist_as_strings` — `Database.SqlQuery<string>` raw scalar reading `Status` from `ApplicationVersions` (`SELECT [Status] FROM [ApplicationVersions]`) returns `"Draft"` (not `0`).

Guard pattern at the top of each test:

```csharp
var cs = Environment.GetEnvironmentVariable("QAB_TEST_SQLSERVER");
if (string.IsNullOrWhiteSpace(cs)) return; // SQL smoke disabled: set QAB_TEST_SQLSERVER to run
```

Use a unique database per run (`Database=QAB_Smoke_{Guid.NewGuid():N}` in the connection string) and `Database.EnsureDeleted()` in `DisposeAsync`.

- [ ] **Step 2: Run the suite without the variable**

Run: `dotnet test tests/QuestionAppBuilder.Infrastructure.Tests` — expected: PASS (smoke tests early-return).

- [ ] **Step 3: Run the suite against a real SQL Server**

Set `$env:QAB_TEST_SQLSERVER` to a working SQL Server connection string (LocalDB is fine: `Server=(localdb)\MSSQLLocalDB;Integrated Security=true`), then run the suite. Expected: PASS — unique index rejects duplicates, rowversion throws `DbUpdateConcurrencyException`, enums read back as `"Draft"`.

- [ ] **Step 4: Generate and eyeball the migration script**

Run: `dotnet ef migrations script --project src/QuestionAppBuilder.Infrastructure --startup-project src/QuestionAppBuilder.Infrastructure`
Expected: script creates the 13 tables, the unique indexes, the rowversion column, and the two seed rows. Note in the run log whether any operation is a table rebuild on non-empty upgrade (none expected on initial create).

- [ ] **Step 5: Fill the run log**

Record exact commands and observed output in `docs/superpowers/plans/notes/2026-10-15-sql-server-verification.md`.

- [ ] **Step 6: Final full run and commit**

Run: `dotnet build` + `dotnet test`. Expected: green.

```powershell
git add -A
git commit -m "test: env-gated SQL Server smoke suite and migration verification log"
```

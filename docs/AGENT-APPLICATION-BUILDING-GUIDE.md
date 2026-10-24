# QuestionAppBuilder: application-building manual for agents

Verified against the working-tree implementation on 2026-10-22. This includes features that may not yet be committed, packaged, or deployed. Check the running host before assuming it has every feature below.

## 1. Purpose and reading contract

Use this document together with a business application requirement to **configure an application inside QuestionAppBuilder**. The usual task is to create a versioned questionnaire, not to generate a new ASP.NET application or change the builder's source code.

QuestionAppBuilder is generic. Grants, enrollment, assessments, budgets, surveys, and capacity requests are examples of definitions built from the same primitives. Business terms, thresholds, rates, and option names belong in application configuration. Do not hardcode a business domain into the platform to satisfy a configuration request.

This is an operating manual, not an import format. The worksheet and symbolic identifiers below are planning artifacts. There is no documented endpoint that accepts this whole document, a natural-language requirement, or the worksheet and automatically imports an application.

### Read only what the task needs

1. Always read sections 2–5: execution contract, concepts, feature selection, and navigation.
2. Read the applicable feature sections 6–13.
3. Always apply sections 14–16: construction order, verification, and handoff.
4. Use section 17 for worked examples, section 18 for troubleshooting, and section 19 only for repository/service automation.
5. Section 20 is a ready-to-use prompt. Section 21 identifies implementation sources for resolving ambiguity.

When the current UI or source disagrees with this document, inspect the implementation and report the discrepancy. Do not invent controls, endpoints, or unsupported behavior. The business requirement governs what to build; the current implementation governs what is possible.

### Feature index

- [Questions and answer validation](#6-questions-content-choices-and-validation)
- [Visibility and Other follow-ups](#7-visibility-and-option-linked-follow-ups)
- [Question scoring and points per unit](#8-question-scoring)
- [Groups, totals, and percentages](#9-question-groups-and-calculated-fields)
- [Combined rules and live feedback](#10-combined-validation-and-scoring-rules)
- [Reusable values and dynamic text](#11-dynamic-values-and-text-templates)
- [Documents](#12-upload-document)
- [Access, availability, and reporting](#13-respondent-access-results-and-reporting)
- [Build workflow](#14-requirements-worksheet-and-construction-order), [acceptance tests](#15-acceptance-tests-for-every-configured-application), and [worked recipes](#17-worked-recipes)
- [Copyable agent prompt](#20-copyable-prompt-for-a-building-agent)

## 2. Agent execution contract

Before writing application data, establish:

- Host base URL and route prefixes. Examples here use `https://localhost:61659`, `/ConfigureApp`, `/App`, and `/Reports`; these are configurable.
- Whether the task is to create a new application or update an identified existing draft. Never infer the target solely from a familiar name or reuse application ID 3 from an example.
- Designer access through the host's supported authentication. Use supplied access; do not invent credentials or bypass authorization.
- Business requirements, authoritative wording, field types, requiredness, options, constraints, branching, scoring, and desired result disclosure.
- Whether publication and test-response creation are included in the user's authorization. Build and verify the draft within scope; if publication needs approval, present the completed draft and unresolved decisions first.
- Whether an existing published application already has respondents. New publication does not migrate their responses.

Ask only about consequential missing decisions: eligibility boundaries, required questions, scoring ambiguity, privacy/result disclosure, or conflicting requirements. Make reversible presentation choices yourself and record them. An unsupported requirement is a gap to report, not permission to invent functionality or change platform code.

Keep a compact identifier map while building:

| Planning key | Entity | Runtime identifier to record |
|---|---|---|
| `app` | Application | Application ID and selected draft/version |
| `cat.profile` | Category | Database ID and category LogicalKey when needed |
| `q.firstName` | Question | Element ID, Question ID, and element LogicalKey when needed |
| `opt.other` | Option | Option ID and option LogicalKey when needed |
| `calc.total` | Calculated field | Composition GUID |
| `var.fullName` | Dynamic reusable value | Dynamic variable GUID |

These planning keys are yours; typing `q.firstName` into a label does not create a platform reference. Question ID and Element ID are different identifiers. Composition references to a question use its **owning element's LogicalKey**, not its database Question ID.

## 3. Product model and lifecycle

| Concept | Meaning |
|---|---|
| Application type | A classification used when creating applications; it is not an answer type or business-rule engine. |
| Application | The durable application identity, containing versioned definitions. |
| Draft | Editable definition. Structural changes, settings, rules, and composition are authored here. |
| Published version | Immutable definition made available to new responses. |
| Category | Ordered questionnaire section and runtime navigation unit. Can have conditional visibility. |
| Element | Ordered item in a category: either content or a question. |
| Content block | Non-answering text/instructions. Can receive a dynamic text template. |
| Question | Answer type, requiredness, validation, options where applicable, and scoring rules. |
| Option | Choice with display label and stored value; may own follow-up questions. |
| Response | A respondent's saved answers pinned to a specific published version. |
| Submission | Completed response with stored scoring/composition results. |

Draft saves do not publish. Publishing an updated draft affects new responses; existing responses keep the definition they started with. Do not test a new feature against an old response and conclude the draft change failed.

To modify a published application, use the supported open/start-draft action to create its editable successor. New applications already have an initial draft. Publication creates a new immutable version rather than editing the previous published rows.

Questionnaire settings include display name, submit-button label, navigation mode, respondent access mode, result visibility, and confirmation text. Choose them intentionally rather than inheriting a demo application's settings.

## 4. Map a requirement to the correct feature

| Requirement | Use | Important boundary |
|---|---|---|
| Collect one answer | Ordinary question | Select a supported answer type. |
| Instructions without an answer | Content block | No answer or score is collected. |
| Require an answer with business wording | Required + custom required message | The custom message replaces only the required-answer message. |
| Limit one answer | Question validation settings | Helper text is not validation. |
| Reveal a dependent question/category | Visibility condition | This is different from scoring and combined-rule conditions. |
| Reveal fields when Other is selected | Option-linked follow-up | Other is an ordinary choice, not a special answer type. |
| Award fixed points for one question | Question scoring rule | Additive/First Match controls aggregation. |
| Award numeric answer × rate | Points-per-unit question scoring | Whole nonnegative Number answers; bounded maximum award. |
| Present fixed rows with descriptive columns | Question group | Groups reuse existing questions; no respondent-added rows. |
| Sum values or calculate a percentage | Calculated field | Numeric composition sources; missing numeric answers contribute zero. |
| Validate a relationship between answers | Combined validation rule | Attach to same/later category than all dependencies. |
| Score a relationship between answers | Combined score rule | Fixed point award; optional ordered band group. |
| Reuse/format answers in displayed text | Dynamic value + text template | Display values do not become visibility/scoring inputs. |
| Format DOB or compute age | Date question + dynamic FormatDate/Age | No arbitrary date-format or expression language. |
| Show validation during typing | Combined rule's live-validation checkbox | Server update after autosave; not per-keystroke client evaluation. |
| Collect a file | Upload Document question | One supported file, 10 MiB maximum. |

Do not substitute scoring for validation: a zero-point answer can still be valid. Do not substitute helper text for constraints: “maximum 16” in help does not enforce 16. Do not substitute a dynamic arithmetic value for a combined-rule calculation: dynamic variables are display-only.

## 5. Navigation and save behavior

Default entry points:

| Page | Route / entry |
|---|---|
| Application management | `/ConfigureApp/Applications` |
| Application detail | `/ConfigureApp/Applications/Detail/{applicationId}` |
| Authoring workspace | `/ConfigureApp/Drafts/{applicationId}` |
| Question configuration | `/ConfigureApp/Composition/Index/{applicationId}` |
| Existing filler response | `/App/Responses/{responseId}`; category navigation may add `?category=...` |
| Reporting | `/Reports` when the host registers the Reports surface and grants access |

Use the host's displayed links for creation, starting a response, and reporting detail rather than guessing action URLs. The application detail page provides **Open authoring workspace**. The workspace currently links to Question configuration using the label **Groups & calculations**; the destination heading is **Question configuration**.

Workspace areas include Questionnaire, Settings, Visibility, Scoring guide, Preview, and Review & publish. Select a category/question in the outline to focus its editor. Question configuration contains **Question groups**, **Calculated fields**, **Combined rules**, and **Dynamic text**.

The workspace saves edits asynchronously; wait for its successful save status before changing pages. Structural actions also use draft revision protection. Question configuration has an explicit **Save configuration** action. Save there, verify success, then reload once to verify persistence. Do not assume unsaved composition changes are stored because the ordinary workspace autosaves.

If a conflict says the draft changed elsewhere, retain the intended edit, reload the latest draft, reconcile, and retry. Never overwrite a newer revision with a previously captured full form. Avoid editing one application from concurrent tabs or agents.

**Review & publish** distinguishes blocking errors from warnings. Fix errors; inspect warnings such as overlapping or shadowed numeric scoring ranges. If the overlap is intentional, acknowledge the current warning set through the workspace before publishing. Both the acknowledgement and publication use the current revision; changing the draft requires a fresh review. Do not hide a warning just to obtain a successful publish.

## 6. Questions, content, choices, and validation

### Supported answer types

| Type | Intended use | Configuration notes |
|---|---|---|
| Textbox | Short free text | Optional MaxLength. Use required/custom message as needed. |
| Textarea | Longer free text | Optional MaxLength. Same basic text validation semantics. |
| Number | Numeric answer | Optional MinValue/MaxValue; optional Whole numbers only. |
| Dropdown | One selection | Add options with labels and values. |
| RadioGroup | One visible choice | Add options; useful for a small mutually exclusive set. |
| Checkboxes | Multiple selections | Optional MinSelections/MaxSelections. |
| UploadDocument | One document | PDF, DOCX without VBA, or UTF-8 TXT; up to 10 MiB. |
| Date | Calendar date | Canonical stored text `yyyy-MM-dd`; use a true Date question for date operations. |

Requiredness applies to applicable/visible questions. Optional does not mean malformed or out-of-range supplied answers are valid. Configure numeric minimum 0 explicitly for nonnegative quantities; enable whole numbers for counts. A required count may legitimately be zero when allowed by its bounds.

Textbox/Textarea answers have a platform maximum of 4,000 characters even when MaxLength is blank. Set a smaller MaxLength when the business requires it; do not promise unrestricted narrative length.

Use **Required-answer message** for wording such as “Enter the number of requested spaces.” Blank configuration falls back to “A response is required.” Do not promise arbitrary custom messages for every built-in length/range/type error; combined validation rules have their own configurable message.

Content blocks have label/help text but no respondent answer. Use them for section explanations or dynamic summaries. Repeated rows are ordinary questions, not additional content fields masquerading as input controls.

For options, supply clear labels and meaningful values. Rules reference option identities, not a fuzzy match on visible labels. Prefer stable machine-oriented values such as `nonprofit` and user-facing labels such as “Registered nonprofit.” After changing answer types or deleting options, review dependent rules and follow-ups before publishing.

### Editing and organization

The detail/workspace surfaces support editing, removal, ordering, and duplication of categories/elements and question settings. Repeated actions may be icons with accessible names/tooltips. Follow-up branches move/copy with their owners. Deleting a parent or option can delete its branch; inspect the affected subtree rather than treating it as a single-row removal.

Duplication is a starting point, not verification. Check copied wording, option ownership, visibility sources, external references, scoring, and dynamic templates. Internal references are remapped by supported copy operations; external dependencies still require review.

## 7. Visibility and option-linked follow-ups

### Visibility conditions

Create/configure conditions in the workspace's **Visibility** section, then assign the desired condition in **Show when** on the supported target.

- **Always show**: no additional condition selected. A question inside a hidden category or an unselected follow-up branch still does not appear.
- **All checks**: every predicate in the selected condition must pass.
- **Any check**: at least one predicate in that condition must pass.
- A displayed condition number is an identifier, not a built-in rule. Inspect its actual predicates.

Predicates include Answered, Unanswered, HasAnyOption, HasAllOptions, and numeric Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual. Do not assume arbitrary string comparisons, regex, date comparisons, calculated-field visibility, or dynamic-variable visibility exist.

A hidden source counts as **Unanswered**. Other predicates require a visible, answered source. Consequently, a downstream Unanswered condition can match when the source question's own branch is hidden; test that case if using Unanswered to reveal a follow-up.

Example: condition “Large request” uses Requested grant amount **GreaterThan 50000**. Assign it to Detailed budget justification. This condition considers only its configured predicate; Years serving the community is irrelevant unless explicitly added as another predicate.

With one predicate, All and Any produce the same result. With two predicates—amount > 50000 and experience < 2—All requires both; Any requires either. Use exact boundary operators: `>` excludes 50000; `>=` includes it.

Arrange visibility dependencies before dependent content/questions; let readiness validation catch invalid references or dependency order. Prefer category/question visibility for supported branching. **Independent visibility assigned to a plain content element has an inherited runtime limitation in this implementation; do not rely on it without a specific verification.** Use a conditional category or a dynamic content template's hide-until-available behavior where appropriate.

### Other and nested inputs

1. Create a choice question (Dropdown, RadioGroup, or Checkboxes).
2. Add an option labeled Other; use the Add Other convenience action if available.
3. Under that option, add a follow-up question, for example Textbox “Specify the other organization type.”
4. Configure the follow-up's requiredness and constraints normally.
5. Test selecting and deselecting the option.

An option may own multiple follow-up questions, including choice questions with their own descendants. Follow-ups must remain in the same category as their parent. Parent selection, ancestor visibility, and any additional visibility condition all apply. Unsupported cross-category/cyclic ownership must not be forced through direct database changes.

Hidden branch answers can remain in an in-progress response for re-selection, but are excluded from applicable required checks and scoring. Their presence in storage must not be treated as permission to display them in dynamic text.

## 8. Question scoring

### Rule families and aggregation

| Family | Eligibility |
|---|---|
| Presence | A qualifying answer is present. |
| Selection | Selected options satisfy AnyOf or AllOf membership against configured options. |
| NumericRange | A Number answer falls within the configured bounds and inclusive/exclusive flags. |

**AnyOf** means at least one configured option is selected. **AllOf** means every configured option is selected; it does not mean the answer contains only those options. A single Selection rule awards its points once when matched, not once for each selected option. To give different points to each selected choice, create separate rules.

**Additive** sums all matching question rules. **FirstMatch** awards only the first matching rule in rule order. Order is therefore business logic. With bands [0,2), [2,5), [5,100], test 0, 2, 5, and 100 and their neighboring values. Avoid unintended overlap or gaps.

Fixed points are nonnegative integer awards. Readiness validates supported bounds and aggregate score capacity. Do not model penalty points or unrestricted decimal scoring unless supported by a separate implemented extension.

### Points per unit

Configure a Number question with:

- Whole numbers only enabled.
- An explicit nonnegative minimum.
- NumericRange rule family and **Points per unit** award method.
- A nonnegative integer rate, and either a maximum answer or an optional maximum-points cap that bounds the award.

For answer `n`, eligible award is `n × rate`, capped by MaximumPoints when set. Eligibility is checked first. The cap belongs to the rule; it does not reject an answer exceeding the cap threshold. Use question bounds or validation to reject answers.

Example: “Participants served,” minimum 0, whole numbers, rate 2, cap 30: 8 earns 16 points; 20 earns 30 points. If question maximum is 16, answer 20 is invalid regardless of the score cap.

Per-rule maximum award is 1,000,000. Invalid, negative, fractional, hidden, or absent answers do not receive a per-unit award; there is no implicit rounding. Additive and FirstMatch still apply. A broad first fixed rule can prevent a later per-unit rule from ever running in FirstMatch mode.

## 9. Question groups and calculated fields

### Question groups

Groups are presentation metadata over existing questions in one category. They can display a table on desktop and cards on narrow screens, or a card arrangement. Informational columns hold fixed descriptive text; each row links to one ordinary question.

Create the questions first, add the group, select its category, set information-column headings, then add questions as rows and populate the cells. Each row must have matching cells for the configured columns. A question cannot be placed in multiple groups. A group is not a repeating collection and cannot create respondent-added rows or a spreadsheet with independently editable informational cells.

For a capacity table, a row might contain fixed Age group, Schedule, and Annual rate cells plus a Number answer for requested spaces. The rate cell is descriptive; writing a rate there does not create a calculation or scoring rule.

### Calculated fields

Calculated fields are read-only derived numeric results assigned to a category:

- **Sum**: sum one or more sources.
- **Percentage**: exactly two ordered sources, numerator / denominator × 100.

Sources are numeric Question answers, constants, existing Calculations, and OptionSelected indicators (1 when selected, 0 otherwise). References are stable identifiers; calculations may depend on other calculations, but cycles are invalid.

Blank numeric answers and hidden numeric questions contribute 0. This supports totals but does not satisfy required-answer validation. A denominator of 0 produces an unavailable result. Do not interpret unavailable as 0%.

Examples: Total spaces = A + B + C. Share of full-time spaces = Full-time spaces / Total spaces × 100. For negative constants or chained totals, verify that the result reflects the business meaning; ordinary calculated fields are not an arbitrary formula editor.

## 10. Combined validation and scoring rules

Combined rules use the numeric sources above and comparisons `=`, `!=`, `>`, `>=`, `<`, `<=`.

Each rule has a category, optional target question, purpose, optional applicability conditions, and one or more checks. **All applicability conditions must pass** before the rule runs. Checks use **all** or **any**, as configured.

- **Validation:** checks express the state that is valid. When applicable checks fail, show the configured error message.
- **Score:** when applicable checks pass, award the configured fixed points.

For “sum must not exceed 16,” use check **Total spaces <= 16**. Do not configure `Total spaces > 16` as the validation check; that would describe the invalid state as passing.

Attach validation to the category containing the latest dependency, or a later category. Dependencies include those reached through calculations and option conditions. Future-category dependencies are rejected. A target question must be in the rule's category; choose an appropriate visible question for the error. Without an explicit target, the engine uses the first visible question in the category. A hidden explicit target means the rule is not applied, so do not attach an always-required total constraint to a conditional question.

Both validation and score rules require a visible target question to execute. A category containing only content/calculated fields has no default target; selecting “First visible question” there cannot enforce a rule or award points. Place the rule in an appropriate category containing an applicable question.

The target controls error placement/applicability, not which source edit triggers reevaluation. A rule using three source answers can be evaluated after any of those answers changes; it does not only run when typing into its target.

### Live feedback and navigation

Enable **Show validation while answering** on a combined validation rule for feedback in the current category after the background save (approximately a 600 ms typing pause plus request time). It requires JavaScript and a successful server response; it is not synchronous per-keystroke validation.

All combined rules still participate in normal sequential forward-navigation and final submission validation. Backward navigation and Free navigation do not become validation gates just because a combined rule exists. Save-in-progress behavior must allow incomplete responses. Do not promise that every Save or Previous click blocks on all errors. Final submission validates applicable answers across the response.

Use the live flag when the requirement is “show a total-limit error while the respondent edits this category.” Test both an error and its disappearance after correction. Cross-category edits may not display a later category's message on the current page.

### Combined scoring bands

Score rules award fixed points. Without a band group, matching rules can contribute independently. Rules sharing the same band group in the same category award only the first matching rule in configured order. Band names are compared without case sensitivity after trimming. They do not create global exclusivity across categories.

Example: percentage >= 75 earns 10; >= 50 earns 5; >= 25 earns 2. Put them in one band group, highest threshold first. Otherwise overlapping checks may award several bands. Combined awards contribute category/total results; inspect traces rather than assuming they replace ordinary question awards.

## 11. Dynamic values and text templates

### Two separate steps

1. Create a **reusable value** that reads, combines, or formats data.
2. Create a **text template** that inserts it into a supported display location.

A reusable value alone does not create a visible questionnaire field. Templates consist of literal parts and inserted-value parts selected in the builder. `{{FirstName}}`, `<<Question>>`, JavaScript, C#, and `.ToFormat(...)` are not executable template syntax. Do not paste expression strings and expect evaluation.

The variable's **Available in** setting controls category applicability: All categories leaves it globally available; choosing a category makes it unavailable while that category is hidden. It does not place a field in that category or restrict which templates may reference the variable.

### Sources and operations

Sources: text, number, or date constants; question answers; other dynamic variables; calculated fields; and Today. Types are Text, Number, Date, and TextList.

| Operation | Inputs and result |
|---|---|
| Value | One source, retaining its type. |
| JoinText | One or more Text/TextList sources; configurable separator and skip-empty behavior. |
| FormatDate | One Date source → formatted Text. |
| Age | Two dates: birth date first, reference date second → completed years. Use Today as reference for current age. |
| Add / Multiply | Two or more Number sources. |
| Subtract / Divide | Exactly two Number sources, in operand order. |

Supported date formats: `yyyy-MM-dd`, `MM-dd-yyyy`, `dd-MM-yyyy`, `MMM d, yyyy`. Use these exact formats; `YYYY`/`DD` and arbitrary format strings are not substitutes. For full name, join First name, Middle name, Last name with a space and skip-empty enabled. Choice answers expose option labels in definition order; checkbox answers are TextList values. Upload answers expose filenames, not file contents.

Age uses completed calendar years, not days divided by 365. A birth date later than the reference date makes the **derived Age value** Invalid/unavailable. This does not make the Date answer itself invalid or block submission: Date answers currently validate calendar format, and Age is display-only. A requirement to reject future DOBs or enforce an age eligibility limit is not implemented merely by adding this variable. A February 29 anniversary falls on February 28 in non-leap years. The configuration's timezone (default UTC) determines Today from one evaluation instant. Choose it deliberately when date boundaries matter.

There is no general expression interpreter, arbitrary function calling, rounding/currency formatter, string-condition language, or conversion of arbitrary Textbox text to Date. Typed operands must match. Numeric dynamic arithmetic is for display, not an input to scoring, visibility, or combined-rule checks.

For a display-only cost illustration, use Multiply(quantity, numeric constant 100) for Base cost, Add(Base cost, 50) for Total, Subtract(Total, 100) for Balance, then Divide(Balance, 5) for Monthly amount. Insert the variables in a template. These operations do not collect payments, enforce affordability, or establish currency rounding. Division by zero and decimal overflow produce an invalid/unavailable dynamic value rather than a valid numeric result.

### Display targets

| Template slot | Display location |
|---|---|
| ElementLabel | A question title or content block's text. |
| ElementHelpText | Help/instructions for the selected element. |
| ReviewSummary | Review page before submission. |
| ConfirmationText | Receipt/confirmation after successful submission. |

To display a summary on an ordinary category page, add a content block there and target its label. To display the same variable in multiple places, create templates for those targets; do not duplicate the computation unnecessarily.

Each element supports one template per slot. The application supports one ReviewSummary and one ConfirmationText template. Combine multiple values/paragraphs into the ordered parts of that template; duplicate slot/owner combinations fail validation.

Missing, hidden/not-applicable, and invalid sources are unavailable, not silently zero. Provide fallback text for inserted parts as appropriate. JoinText can omit empty entries; it must not expose answers hidden by branching. Hide-until-available applies to content labels/review summaries, not arbitrary question titles. Keep useful static question labels: empty resolved question titles fall back to static text.

When **Hide when unavailable** is enabled on a supported target, any unavailable inserted value hides the entire target even if a fallback was supplied. Leave hiding disabled when respondents should see the fallback text.

The numeric calculated-field layer has different missing behavior: a blank numeric question contributes 0 there. If a dynamic variable reads such a calculated field, it receives that calculation's result; it does not restore missing-source semantics.

Displayed text is encoded; neither respondent answers nor literal parts execute HTML/scripts or recursively expand another template. Forward references can be used for display and stay unavailable until answered. Submission freezes the resolved values/text and evaluation time; viewing a receipt later does not age the applicant again.

## 12. Upload Document

One file per question: PDF, DOCX without VBA, or UTF-8 TXT, maximum 10 MiB. Configure separate questions when multiple distinct documents are needed. Required validation and Presence scoring are available. An upload can be an option-linked follow-up.

Runtime upload/removal uses JavaScript and protected multipart requests. The designer preview simulates a file and does not store its bytes; a real disposable response is needed to test actual upload/download behavior.

The default service stores bytes separately in SQL Server. Ownership/access checks protect downloads; submitted files cannot be replaced or removed. Hidden-branch files are retained during editing and deleted on submission. Reports/CSV expose filenames and authorized attachment access, not public file URLs.

File format checks are not malware scanning. A requirement for virus scanning, different extensions, larger files, or external object storage is host/service integration work, not an existing per-question configuration option. Do not claim it is configured by setting a question label.

## 13. Respondent access, results, and reporting

Navigation modes are Sequential and Free. Sequential enforces the supported forward-progression checks; Free allows category movement without making each move a completion gate. Both still require valid applicable answers for final submission.

Respondent access modes are host sign-in and email-link access. The host owns authentication, design/fill/report policies, email delivery, public origin, and infrastructure. Email links use scoped one-time tokens and require host configuration. Do not equate email-link access with an unrestricted anonymous public form.

Result visibility offers **ConfirmationOnly** (confirmation without scores), **Total** (total score), **CategoryBreakdown** (total and category scores), and **ConfirmationAndAnswerReview** (confirmation, total, and a read-only answer review across all visible categories). Each receipt also links to the respondent's submitted application. The separate **SubmittedApplicationVisibility** setting controls whether that reopened application shows scores: **WithoutScores** hides them; **WithScores** shows the stored total and positive question, category, calculated-value, and combined-award results. Reopened applications are read-only and let respondents browse visible categories. Category visibility and dynamic wording are frozen at submission; the application is never reevaluated when reopened. Select both disclosures explicitly. Confirmation text can be customized independently. Designer preview, respondent receipts, and authorized analyst reports serve different audiences; verify them separately.

Application detail also controls availability independently of publication. **AcceptNewResponses** prevents new starts when disabled. **AcceptSubmissions** prevents final submission when disabled and also disables new starts; existing in-progress responses can still save. First publication enables both flags; later publications preserve availability. Check these switches when a correctly published application cannot be started or submitted. Do not change availability on an existing live application without authorization for that operational change.

The public landing page is `/App/{applicationId}`; use its Start action to create a response. The respondent's **Mine** surface lists their accessible responses under the host's fill policy. Review precedes final submission, and successful submission leads to a receipt. Submitted responses are not editable drafts.

Reports support submitted-response inspection, score traces/category totals, composition results, and CSV export through the registered Reports surface. Stored submission results are used rather than recomputing against a later draft. Dynamic text uses its frozen submitted snapshot. CSV document values are filenames. Host report authorization is separate from respondent ownership.

Default report routes include `/Reports/Submissions` (filterable list), `/Reports/Detail/{submissionId}`, and `/Reports/Export`. Filters include application, version, dates, and owner-reference search. Answer-inclusive CSV export requires an application and version selection. Date filters use the report display timezone. Inspect export scope before sharing it; a respondent's restricted receipt is not equivalent to an analyst export.

Do not promise a general approval workflow, reviewer assignment, payments, arbitrary external integrations, spreadsheet import, or respondent-added repeating sections merely because the questionnaire can collect related data. Treat such requirements as capability gaps until verified in current code.

## 14. Requirements worksheet and construction order

Before authoring, produce a compact map like this. It is a planning format, not uploadable JSON/YAML.

```yaml
application:
  name: Equipment Support Request
  target: new                 # or an explicitly identified existing application
  navigation: Sequential
  access: HostSignIn
  resultDisclosure: decision-needed
  publication: draft-only     # use the user's actual authorization
categories:
  - key: cat.request
    name: Request
questions:
  - key: q.laptops
    category: cat.request
    label: Laptops requested
    type: Number
    required: true
    requiredMessage: Enter the number of laptops requested.
    minimum: 0
    wholeNumbersOnly: true
    maximum: 16
calculations:
  - key: calc.total
    operation: Sum
    sources: [q.laptops, q.tablets, q.monitors]
rules:
  - key: rule.capacity
    purpose: Validation
    category: cat.request
    target: q.laptops
    validWhen: calc.total <= 16
    message: Request no more than 16 items in total.
    live: true
```

The abbreviated example references two questions not shown; define them before creating the calculation. For a real build, every reference must resolve. Add separate sections for options/follow-ups, visibility, groups, scoring, dynamic variables/templates, and expected test outcomes only when needed.

Build in this order:

1. Inspect the host, target application, current draft/version, and available controls. Record what already exists.
2. Resolve consequential ambiguities and map requirements to supported features. Identify unsupported requirements explicitly.
3. Create the application/type if needed and set application-level settings.
4. Create ordered categories, content blocks, and ordinary questions. Capture identifiers.
5. Set required messages, validation limits, options, and option-linked follow-ups.
6. Configure visibility predicates and assign them to targets; verify source ordering.
7. Configure ordinary scoring and points-per-unit rules; verify order and boundaries.
8. Create presentation groups, then calculated fields in dependency order.
9. Create combined validation/scoring rules, targets, live flags, and ordered band groups.
10. Create dynamic variables, then templates attached to actual display targets. Set timezone/fallbacks.
11. Save each surface and verify reload persistence. Review readiness issues and fix them.
12. Preview branches, validation, calculated results, and scores with the test matrix below.
13. Publish if authorized. Start a fresh test response to verify runtime behavior; do not reuse one pinned to an old version.
14. Submit only an authorized test response. Verify receipt/report output and produce a concise handoff.

Use one writer per application. Parallel research/review can be efficient; parallel mutation of one revision-protected draft creates conflicts. For large definitions, work in coherent category-sized batches and record progress so a retry does not create duplicates.

## 15. Acceptance tests for every configured application

Choose cases based on the requirement, with explicit expected outcomes. A successful save or a screenshot of the editor is not proof that the application behaves correctly.

| Area | Minimum useful cases |
|---|---|
| Persistence | Save, reload, confirm settings and counts; confirm new items were not duplicated. |
| Requiredness | Blank applicable required answer shows intended message; optional blank is accepted. |
| Bounds | Min, max, just below/above; fractional count when whole-only; zero where permitted. |
| Choices | Each meaningful branch; min/max selection counts; Other selected/deselected/reselected. |
| Visibility | Each predicate true/false, All vs Any, exact boundary, hidden ancestor/category. |
| Scoring | Hand-computed rule/category/total scores; overlaps, FirstMatch order, per-unit cap. |
| Calculations | Normal values, zeros, missing inputs, hidden sources, zero denominator. |
| Combined rules | Passing/failing checks, applicability gate, hidden target, category dependency. |
| Live validation | Error after save pause; correction clears it; target need not be the edited source. |
| Navigation | Forward, Back, Free/Sequential behavior as configured, final-submit enforcement. |
| Dynamic text | Complete inputs, missing optional middle name, hidden source, fallback, date and age boundary. |
| Uploads | Supported actual file, invalid/oversize file, removal/replacement before submit, authorized download. |
| Versioning | New response uses new publication; existing response retains its pinned version. |
| Results | Respondent disclosure matches settings; analyst report/CSV matches stored submission. |

Use synthetic data, clearly named test responses, and a disposable environment where available. Do not erase real submissions or rewrite published definitions to simplify a test. If something cannot be exercised due to access/environment limitations, mark it unverified rather than passed.

## 16. Required agent handoff

Return:

- Application name/ID, draft or published status, and authoring/configuration links.
- Categories/questions created or changed, with a concise requirement-to-configuration mapping.
- Important rules, numeric thresholds, scoring mode/order, and dynamic display targets.
- Assumptions and unsupported or deferred requirements.
- Tests performed with expected/actual outcomes, plus any unverified paths.
- Publication version and fresh-response entry link when publication was performed.
- Any host prerequisites still needed, without claiming they were configured.

Do not say “complete” when critical requirements remain unsupported, readiness fails, or changes were never saved. Distinguish “draft configured,” “published,” and “runtime verified.”

## 17. Worked recipes

### A. Three quantities must total no more than 16

Create Laptops, Tablets, Monitors as required whole Number questions, minimum 0, in one category. Add Sum calculation Total items with all three sources. Add combined Validation rule in that category, target Laptops, check Total items <= constant 16, message “Request no more than 16 items in total,” live enabled.

Test 5+5+6 = 16 passes; 5+5+7 = 17 fails. Change Tablets to 4: total 16 clears the error even though Laptops is the target. Blank required fields still fail requiredness even though the Sum treats blanks as zero. Back navigation is not proof that the constraint is absent; test live feedback, forward navigation, and final submission.

### B. Number of participants × 2 points

Create Participants served: Number, required, whole-only, min 0, max 100. Add NumericRange scoring rule, Points per unit, rate 2, cap 30 if the requirement specifies one. Use appropriate eligibility bounds or leave the supported per-unit range unbounded if all valid values qualify. Choose Additive only if other matching rules should also contribute.

Test 0→0, 8→16, 20→30 with that cap. Fractional/negative answers are invalid. If another broad rule is first in FirstMatch mode, it may win instead; inspect rule order.

### C. Name, DOB, age, and confirmation

Create First name and Last name required Textbox questions, optional Middle name Textbox, and required Date of birth Date question. Create Full name = JoinText of the three names, separator space, skip-empty. Create Formatted DOB = FormatDate(DOB) using MM-dd-yyyy. Create Age = Age(DOB, Today), with the application's intended timezone.

Add a content block named Applicant summary in a later category. Create its ElementLabel template from literal “Applicant: ” + Full name + literal “; date of birth: ” + Formatted DOB + literal “; age: ” + Age. Configure sensible unavailable fallbacks. Add a ConfirmationText template using Full name if desired.

Save configuration, preview, publish if authorized, and start a fresh response. Verify an empty middle name does not create an awkward gap. Compare age around the birthday. Confirm the summary appears on the intended page and confirmation appears only after submission. The submitted age remains frozen when the receipt is read later.

### D. Other with additional evidence

Create Organization type as RadioGroup with Nonprofit, Public agency, Other. Under Other add a required Textbox “Describe your organization type” and, if required by the business, an UploadDocument “Supporting document.” These are normal same-category follow-ups.

Selecting Other reveals both. Selecting Nonprofit hides them and removes their applicable required/scoring burden. Re-selecting Other during editing can restore retained answers. Verify actual file behavior with a test response, not solely preview.

## 18. Troubleshooting and capability limits

| Symptom | Check |
|---|---|
| Cannot find Question configuration | The workspace link is currently labeled Groups & calculations; use the route in section 5. |
| Dynamic value is not visible | Add a template and attach it to a supported slot; a variable is not an element. |
| Template displays literal braces/code | Use structured literal/value parts; no free-form expression interpreter exists. |
| New setting is absent in filler | Save, publish, start a new response; verify host has current binaries/migrations. |
| Rule only appears sometimes | Inspect conditions, category visibility, explicit target visibility, and source applicability. |
| Previous allows an invalid total | Backward navigation is not the forward-validation gate; use live feedback and test final submission. |
| Total is zero with blanks | Calculated fields treat missing numeric answers as zero; required validation is separate. |
| A score is higher than expected | Inspect Additive overlaps, ungrouped combined bands, and simultaneous ordinary/combined awards. |
| Per-unit rule never contributes | Check FirstMatch order, range eligibility, whole/nonnegative configuration, and cap/maximum. |
| Date formatter rejects source | Use Date, not Textbox; select an exact supported format. |
| Conditional content stays visible | Check the independent content-visibility limitation; use a verified supported alternative. |
| Save reports conflict | Reload and reconcile current revision; do not replay stale full-form payloads. |
| Copy/delete breaks readiness | Inspect referenced questions/options/calculations/templates and repair dangling external refs. |

Configuration limits are validation ceilings, not recommendations for dense pages: up to 100 groups, 200 calculations, 500 combined rules; group up to 8 information columns/200 rows; calculation up to 200 sources; combined rule up to 50 conditions and 50 checks. Dynamic text supports up to 200 variables, 500 templates, 100 sources per variable, and 100 parts per template; text limits are 4000 characters and dependency depth 100. Other validation constraints still apply. Configuration POST accepts at most 500,000 JSON characters within its request-size limit. Split a business experience thoughtfully rather than designing up to each maximum.

## 19. Repository/service automation (optional)

Use the browser UI as the default authoring contract. If the task explicitly provides repository execution and authorizes programmatic creation, use application services through the configured host/DI and current model types. Do not write raw SQL into definition tables or assemble guessed HTTP payloads from this guide.

- `IApplicationDesignService`: create applications/types; open/get drafts; add/edit/remove/duplicate/move categories/elements; add questions/options/follow-ups/rules; visibility operations; publish.
- `IDraftService`: load a snapshot, save a `SaveDraftRequest`, review readiness, publish a `PublishRequest`. `GetWorkspaceAsync` returns draft plus review from a consistent production workspace load.
- `CompositionDefinition`: groups/calculations/rules/dynamic configuration serialized into the version's `CompositionJson`.
- `IDefinitionEvaluator`: shared definition/answer evaluation; use it and readiness validation instead of reproducing business logic in the automation script.

Read the actual signatures before invocation. Preserve all unrelated graph fields. Carry the current `Revision`/expected revision on mutations and reload after structural changes. Database IDs may change across versions; LogicalKey references are the stable composition identities. Generate real unique GUIDs for new composition entities, and resolve references from the graph instead of inventing GUIDs in examples.

For publication with intentional warnings, use the draft publication workflow: `PublishRequest` includes the current revision and exactly the current acknowledged warning-code set. The lower-level `IApplicationDesignService.PublishAsync` rejects warnings rather than accepting that acknowledgement flow. A readiness result from an older revision is not reusable after edits.

HTTP authoring uses authentication, antiforgery, model binding, and revision tokens. The current Composition Save action expects `id`, `expectedRevision`, and serialized `definitionJson`; it is an MVC form action, not a public bulk import API. A full workspace form also contains nested collection fields and checkbox hidden values. Do not replay a captured payload from another draft/session or strip fields on the assumption that saving is a partial patch.

Programmatic automation must be repeatable without duplication: inspect existing state, maintain the key map, apply only the intended changes, validate, then reread. If an operation fails after an uncertain response, inspect whether it already succeeded before retrying.

Host prerequisites: compatible .NET/EF/SQL setup, required migrations, registered Core/Editor and optional Reports surfaces, policies, and email services when applicable. Upload, generic composition, and points-per-unit features have schema migrations. Date/dynamic text reuse existing storage beyond generic composition. See integration documentation for deployment details; do not restart another person's running host or change infrastructure merely to author a draft.

## 20. Copyable prompt for a building agent

```text
Use the attached QuestionAppBuilder application-building manual and my requirements
to configure an application in the existing builder.

Host: <base URL>
Target: <new application, or existing application ID and name>
Access: <how authorized designer access is provided; do not include secrets here>
Publication scope: <draft only, or publish after verification>
Test data scope: <where disposable responses may be created/submitted>
Requirements: <paste the authoritative business requirement>

Read the manual's execution contract, feature map, and relevant feature sections.
Inspect the current target before making changes. Map every requirement to a
supported feature. Ask only about consequential ambiguity; report unsupported
requirements rather than inventing capabilities. Configure the application,
preserve unrelated existing data, use one writer for its draft, and maintain an
identifier map. Verify saves, readiness, branching, validation, scoring, and
dynamic text with explicit test cases. Publish only within the scope above.
Return application links/status, requirement coverage, tests, assumptions, and
remaining gaps. Do not stop after proposing a configuration if you have the
tools, access, and authorization to create it.
```

## 21. Source map and maintenance

This document is self-contained for configuration work. Repository paths below are relative to the repository root and provide verification anchors:

| Concern | Implementation/reference |
|---|---|
| Host integration and deployment | `docs/integration.txt`, `samples/QuestionAppBuilder.SampleHost/README.md` |
| Detailed composition walkthrough | `docs/groups-calculations-and-rules-guide.md` |
| Answer/condition/scoring enums | `src/QuestionAppBuilder.Domain/Enums/` |
| Authoring service contract | `src/QuestionAppBuilder.Application/Services/IApplicationDesignService.cs` |
| Draft contract | `src/QuestionAppBuilder.Application/Authoring/IDraftService.cs` |
| Composition schema | `src/QuestionAppBuilder.Domain/Composition/CompositionDefinition.cs` |
| Combined/group/calculation semantics | `src/QuestionAppBuilder.Application/Composition/CompositionEngine.cs` |
| Dynamic types, operations, limits | `src/QuestionAppBuilder.Application/Composition/DynamicTextEngine.cs` |
| Per-unit award bounds | `src/QuestionAppBuilder.Domain/ScoringAward.cs` |
| Definition and answer evaluation | `src/QuestionAppBuilder.Application/Evaluation/DefinitionEvaluator.cs` |
| Builder routes/save behavior | `src/QuestionAppBuilder.Editor/Controllers/` |
| Runtime navigation/submission | `src/QuestionAppBuilder.Core/Controllers/ResponsesController.cs`, `src/QuestionAppBuilder.Infrastructure/Responses/` |
| Executable usage examples | `tests/QuestionAppBuilder.Browser.Tests/` and `tests/QuestionAppBuilder.Application.Tests/` |

When features change, update their constraints, workflow, examples, and tests together. Record the verified date/version. Do not convert a planned feature into documented availability before implementation and verification.

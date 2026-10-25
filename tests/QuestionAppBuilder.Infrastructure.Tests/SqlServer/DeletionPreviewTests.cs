using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using QuestionAppBuilder.Application.Authoring;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Authoring;
using QuestionAppBuilder.Infrastructure.Persistence;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public sealed class DeletionPreviewTests
{
    [SqlServerFact]
    public Task Category_preview_matches_committed_cascades_and_preserves_published_definition()
        => VerifyDeletionAsync(DraftDeletionKind.Category);

    [SqlServerFact]
    public Task Element_preview_matches_committed_cascades_and_preserves_published_definition()
        => VerifyDeletionAsync(DraftDeletionKind.Element);

    [SqlServerFact]
    public Task Option_preview_matches_committed_cascades_and_preserves_published_definition()
        => VerifyDeletionAsync(DraftDeletionKind.Option);

    [SqlServerFact]
    public async Task Referenced_option_is_blocked_without_changing_revision_or_any_children()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seed = await SeedAsync(database, blockOption: true);
        var coordinator = new DraftDeletionCoordinator(new DatabaseFactory(database));
        var before = await SnapshotAsync(database);

        var preview = await coordinator.PreviewAsync(Target(seed, DraftDeletionKind.Option), seed.Revision);

        preview.CanDelete.Should().BeFalse();
        preview.Blocked.Should().NotBeEmpty();
        (await SnapshotAsync(database)).Should().Be(before, "preview is read-only, including blocked previews");
        Func<Task> confirm = () => coordinator.ConfirmAsync(Request(seed, DraftDeletionKind.Option));
        await confirm.Should().ThrowAsync<DefinitionValidationException>();
        (await SnapshotAsync(database)).Should().Be(before, "a blocked option must not delete its follow-up branch");
    }

    [SqlServerFact]
    public async Task Confirmation_rejects_stale_revision_wrong_version_key_and_application()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var coordinator = new DraftDeletionCoordinator(new DatabaseFactory(database));
        await coordinator.PreviewAsync(Target(seed, DraftDeletionKind.Element), seed.Revision);
        var before = await SnapshotAsync(database);

        Func<Task> wrongVersion = () => coordinator.ConfirmAsync(Request(seed, DraftDeletionKind.Element) with
            { ExpectedVersionId = seed.PublishedVersionId });
        await wrongVersion.Should().ThrowAsync<DraftConflictException>();
        Func<Task> wrongKey = () => coordinator.ConfirmAsync(Request(seed, DraftDeletionKind.Element) with
            { TargetLogicalKey = Guid.NewGuid() });
        await wrongKey.Should().ThrowAsync<NotFoundException>();
        Func<Task> wrongApplication = () => coordinator.ConfirmAsync(Request(seed, DraftDeletionKind.Element) with
            { ApplicationId = seed.ApplicationId + 1000 });
        await wrongApplication.Should().ThrowAsync<NotFoundException>();
        (await SnapshotAsync(database)).Should().Be(before);

        await using (var edit = database.CreateContext())
        {
            var draft = await edit.ApplicationVersions.SingleAsync(version => version.Id == seed.VersionId);
            draft.DisplayName = "Edited after preview";
            draft.RevisionNonce = Guid.NewGuid();
            await edit.SaveChangesAsync();
        }
        var changed = await SnapshotAsync(database);
        Func<Task> stale = () => coordinator.ConfirmAsync(Request(seed, DraftDeletionKind.Element));
        await stale.Should().ThrowAsync<DraftConflictException>();
        (await SnapshotAsync(database)).Should().Be(changed);
    }

    [SqlServerFact]
    public async Task Publication_winning_before_confirmation_load_never_creates_a_clone_or_deletes_published_children()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var factory = new DatabaseFactory(database);
        await new DraftDeletionCoordinator(factory).PreviewAsync(Target(seed, DraftDeletionKind.Element), seed.Revision);
        var gate = new PausedFactory(database);
        var confirmation = new DraftDeletionCoordinator(gate).ConfirmAsync(Request(seed, DraftDeletionKind.Element));
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
        try
        {
            var drafts = new DraftService(factory, new DefinitionEvaluator(), actor: new TestActor());
            var review = await drafts.ReviewAsync(seed.ApplicationId);
            review.Errors.Should().BeEmpty();
            await drafts.PublishAsync(new PublishRequest(seed.ApplicationId, seed.Revision,
                review.Warnings.Select(warning => warning.Code).ToHashSet()));
        }
        finally { gate.Release.TrySetResult(); }

        Func<Task> completeConfirmation = async () => await confirmation;
        await completeConfirmation.Should().ThrowAsync<NotFoundException>();
        await using var verify = database.CreateContext();
        (await verify.ApplicationVersions.CountAsync()).Should().Be(2, "confirmation must never use legacy published-version cloning");
        (await verify.ApplicationVersions.AnyAsync(version => version.Status == VersionStatus.Draft)).Should().BeFalse();
        (await verify.Elements.AnyAsync(element => element.Id == seed.RootId)).Should().BeTrue();
        (await verify.Elements.AnyAsync(element => element.Id == seed.GrandchildId)).Should().BeTrue();
        (await verify.Applications.SingleAsync()).CurrentVersionId.Should().Be(seed.VersionId);
    }

    [SqlServerFact]
    public async Task Publication_after_confirmation_load_rolls_back_all_child_deletions()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var drafts = new DraftService(new DatabaseFactory(database), new DefinitionEvaluator(), actor: new TestActor());
        var review = await drafts.ReviewAsync(seed.ApplicationId);
        review.Errors.Should().BeEmpty();
        string? publishedSnapshot = null;
        var interceptor = new BeforeSaveInterceptor(async () =>
        {
            await drafts.PublishAsync(new PublishRequest(seed.ApplicationId, seed.Revision,
                review.Warnings.Select(warning => warning.Code).ToHashSet()));
            publishedSnapshot = await SnapshotAsync(database);
        });
        var coordinator = new DraftDeletionCoordinator(new InterceptedFactory(database, interceptor));

        Func<Task> confirm = () => coordinator.ConfirmAsync(Request(seed, DraftDeletionKind.Element));
        await confirm.Should().ThrowAsync<DraftConflictException>();

        interceptor.Calls.Should().Be(1, "publication runs after the deletion graph is loaded and before its first write");
        publishedSnapshot.Should().NotBeNull();
        (await SnapshotAsync(database)).Should().Be(publishedSnapshot,
            "the stale confirmation must roll back every deletion and preserve the concurrently published graph");
        await using var verify = database.CreateContext();
        (await verify.ApplicationVersions.CountAsync()).Should().Be(2);
        (await verify.ApplicationVersions.AnyAsync(version => version.Status == VersionStatus.Draft)).Should().BeFalse();
        (await verify.Applications.SingleAsync()).CurrentVersionId.Should().Be(seed.VersionId);
    }

    [SqlServerFact]
    public async Task Malformed_composition_preview_blocks_confirmation_without_partial_removal()
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        await using (var corrupt = database.CreateContext())
        {
            (await corrupt.ApplicationVersions.SingleAsync(version => version.Id == seed.VersionId)).CompositionJson = "{ invalid";
            await corrupt.SaveChangesAsync();
        }
        var coordinator = new DraftDeletionCoordinator(new DatabaseFactory(database));
        var before = await SnapshotAsync(database);
        var preview = await coordinator.PreviewAsync(Target(seed, DraftDeletionKind.Category), seed.Revision);
        preview.CanDelete.Should().BeFalse();
        preview.Warnings.Should().NotBeEmpty();
        Func<Task> confirm = () => coordinator.ConfirmAsync(Request(seed, DraftDeletionKind.Category));
        await confirm.Should().ThrowAsync<DefinitionValidationException>();
        (await SnapshotAsync(database)).Should().Be(before);
    }

    private static async Task VerifyDeletionAsync(DraftDeletionKind kind)
    {
        await using var database = await SqlServerDatabase.CreateAsync();
        var seed = await SeedAsync(database);
        var coordinator = new DraftDeletionCoordinator(new DatabaseFactory(database));
        var before = await SnapshotAsync(database);
        var publishedBefore = await PublishedSnapshotAsync(database, seed.PublishedVersionId);
        var preview = await coordinator.PreviewAsync(Target(seed, kind), seed.Revision);

        preview.CanDelete.Should().BeTrue();
        preview.VersionId.Should().Be(seed.VersionId);
        preview.Revision.Should().Equal(seed.Revision);
        JsonSerializer.Serialize(preview.Removed).Should().Contain("Grandchild amount");
        JsonSerializer.Serialize(preview.AutomaticallyChanged).Should().Contain("Retained banner");
        preview.NeedsRepair.Should().NotBeEmpty("surviving configured values refer to the removed branch");
        preview.NeedsRepair.Select(item => item.Name).Should().Contain("Retained calculation")
            .And.Contain("Retained variable").And.Contain("ReviewSummary",
                "the preview must explain indirect consumers as well as direct missing references");
        preview.NeedsRepair.Should().NotContain(item => item.Name.StartsWith("Unrelated"),
            "independent configured values do not need repair");
        if (kind != DraftDeletionKind.Category)
            preview.AutomaticallyChanged.Select(item => item.Name).Should().Contain("Automatic target rule");
        (await SnapshotAsync(database)).Should().Be(before, "reading the impact or canceling it must not change the draft");

        await coordinator.ConfirmAsync(Request(seed, kind));

        await using var verify = database.CreateContext();
        (await verify.Elements.AnyAsync(element => element.Id == seed.ChildId || element.Id == seed.GrandchildId)).Should().BeFalse();
        (await verify.QuestionOptions.AnyAsync(option => option.Id == seed.OtherOptionId)).Should().BeFalse();
        (await verify.VisibilityConditions.AnyAsync(condition => condition.Id == seed.ConditionId)).Should().BeFalse();
        (await verify.Elements.SingleAsync(element => element.Id == seed.RetainedContentId)).VisibilityConditionId.Should().BeNull();
        (await verify.Categories.SingleAsync(category => category.Id == seed.RetainedCategoryId)).VisibilityConditionId.Should().BeNull();
        (await verify.Elements.AnyAsync(element => element.Id == seed.RootId)).Should().Be(kind == DraftDeletionKind.Option);
        (await verify.Categories.AnyAsync(category => category.Id == seed.SourceCategoryId)).Should().Be(kind != DraftDeletionKind.Category);
        var persisted = await verify.ApplicationVersions.SingleAsync(version => version.Id == seed.VersionId);
        persisted.Status.Should().Be(VersionStatus.Draft);
        persisted.RevisionNonce.ToByteArray().Should().NotEqual(seed.Revision);
        var composition = CompositionDefinition.Read(persisted.CompositionJson);
        composition.Groups.Select(group => group.Name).Should().Equal("Retained group");
        composition.DynamicText.Templates.Should().NotContain(template => template.OwnerKey == seed.GrandchildKey);
        composition.Calculations.Should().Contain(calculation => calculation.Name == "Retained calculation");
        composition.Calculations.Any(calculation => calculation.Name == "Branch calculation").Should().Be(kind != DraftDeletionKind.Category);
        composition.Rules.Any(rule => rule.Name == "Branch rule").Should().Be(kind != DraftDeletionKind.Category);
        composition.DynamicText.Variables.Any(variable => variable.Name == "Branch variable").Should().Be(kind != DraftDeletionKind.Category);
        composition.DynamicText.Templates.Should().Contain(template => template.Slot == DynamicTextSlot.ReviewSummary,
            "a surviving template with a broken source is reported for repair, not silently deleted");
        (await PublishedSnapshotAsync(database, seed.PublishedVersionId)).Should().Be(publishedBefore);
    }

    private static DraftDeletionTarget Target(Seed seed, DraftDeletionKind kind)
        => new(seed.ApplicationId, kind, kind switch
        {
            DraftDeletionKind.Category => seed.SourceCategoryId,
            DraftDeletionKind.Element => seed.RootId,
            _ => seed.OtherOptionId
        });

    private static ConfirmDraftDeletionRequest Request(Seed seed, DraftDeletionKind kind)
        => new(seed.ApplicationId, seed.VersionId, kind, Target(seed, kind).TargetId,
            kind switch { DraftDeletionKind.Category => seed.SourceCategoryKey, DraftDeletionKind.Element => seed.RootKey, _ => seed.OtherOptionKey }, seed.Revision);

    private static async Task<Seed> SeedAsync(SqlServerDatabase database, bool blockOption = false)
    {
        await using var db = database.CreateContext();
        await db.Database.MigrateAsync();
        var root = new Element { Kind = ElementKind.Question, Label = "Choose branch", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.RadioGroup } };
        var other = new QuestionOption { Label = "Other branch", Value = "other", SortOrder = 1 };
        root.Question.Options.AddRange([other, new QuestionOption { Label = "No branch", Value = "none", SortOrder = 2 }]);
        if (blockOption) root.Question.Rules.Add(new ScoringRule { Family = RuleFamily.Selection, Points = 3,
            MembershipOperator = MembershipOperator.AnyOf, Options = [new ScoringRuleOption { QuestionOption = other }] });
        var child = new Element { Kind = ElementKind.Question, Label = "Child choices", SortOrder = 2, ParentOption = other,
            Question = new Question { AnswerType = AnswerType.Dropdown } };
        var nestedOption = new QuestionOption { Label = "Nested branch", Value = "nested", SortOrder = 1 };
        child.Question.Options.Add(nestedOption);
        var grandchild = new Element { Kind = ElementKind.Question, Label = "Grandchild amount", SortOrder = 3, ParentOption = nestedOption,
            Question = new Question { AnswerType = AnswerType.Number } };
        var sourceCategory = new Category { Name = "Branch category", SortOrder = 1, Elements = [root, child, grandchild] };
        var retained = new Element { Kind = ElementKind.Question, Label = "Retained amount", SortOrder = 1,
            Question = new Question { AnswerType = AnswerType.Number } };
        var banner = new Element { Kind = ElementKind.Content, Label = "Retained banner", SortOrder = 2 };
        var condition = new VisibilityCondition { Junction = ConditionJunction.All,
            Predicates = [new VisibilityPredicate { SourceQuestion = root.Question, Operator = ConditionOperator.HasAnyOption,
                Options = [new VisibilityPredicateOption { QuestionOption = other }] }] };
        var retainedCategory = new Category { Name = "Retained category", SortOrder = 2, Elements = [retained, banner], VisibilityCondition = condition };
        banner.VisibilityCondition = condition;
        var localCalculation = new CalculatedField { Name = "Branch calculation", CategoryKey = sourceCategory.LogicalKey,
            Sources = [new ValueSource { Kind = SourceKind.Question, Key = grandchild.LogicalKey }] };
        var externalCalculation = new CalculatedField { Name = "Retained calculation", CategoryKey = retainedCategory.LogicalKey,
            Sources = [new ValueSource { Kind = SourceKind.Calculation, Key = localCalculation.Id },
                new ValueSource { Kind = SourceKind.OptionSelected, Key = other.LogicalKey }] };
        var localVariable = new DynamicVariable { Name = "Branch variable", CategoryKey = sourceCategory.LogicalKey,
            Operation = DynamicOperation.Value, Sources = [new DynamicSource { Kind = DynamicSourceKind.Question, Key = grandchild.LogicalKey }] };
        var externalVariable = new DynamicVariable { Name = "Retained variable", Operation = DynamicOperation.Value,
            Sources = [new DynamicSource { Kind = DynamicSourceKind.Variable, Key = localVariable.Id }] };
        var composition = new CompositionDefinition
        {
            Groups = [new QuestionGroup { Name = "Branch group", CategoryKey = sourceCategory.LogicalKey, Rows = [new GroupRow { QuestionKey = grandchild.LogicalKey }] },
                new QuestionGroup { Name = "Retained group", CategoryKey = retainedCategory.LogicalKey, Rows = [new GroupRow { QuestionKey = retained.LogicalKey }] }],
            Calculations = [localCalculation, externalCalculation,
                new CalculatedField { Name = "Unrelated calculation", CategoryKey = retainedCategory.LogicalKey,
                    Sources = [new ValueSource { Kind = SourceKind.Question, Key = retained.LogicalKey }] }],
            Rules = [new CompositionRule { Name = "Branch rule", CategoryKey = sourceCategory.LogicalKey,
                TargetQuestionKey = grandchild.LogicalKey, Kind = RulePurpose.Validation, Message = "Use a nonnegative amount",
                Checks = [new ValueCheck { Left = new ValueSource { Kind = SourceKind.Calculation, Key = localCalculation.Id },
                    Comparison = Comparison.GreaterThanOrEqual, Right = new ValueSource { Kind = SourceKind.Constant, Value = 0 } }] },
                new CompositionRule { Name = "Automatic target rule", CategoryKey = sourceCategory.LogicalKey,
                    Kind = RulePurpose.Validation, Message = "Check this category", Checks = [new ValueCheck {
                        Left = new ValueSource { Kind = SourceKind.Constant, Value = 1 }, Comparison = Comparison.GreaterThanOrEqual,
                        Right = new ValueSource { Kind = SourceKind.Constant, Value = 0 } }] }],
            DynamicText = new DynamicTextDefinition { Variables = [localVariable, externalVariable,
                new DynamicVariable { Name = "Unrelated variable", Operation = DynamicOperation.Value,
                    Sources = [new DynamicSource { Kind = DynamicSourceKind.Question, Key = retained.LogicalKey }] }], Templates =
                [new DynamicTemplate { Slot = DynamicTextSlot.ElementLabel, OwnerKey = grandchild.LogicalKey,
                    Parts = [new DynamicTextPart { Kind = DynamicPartKind.Literal, Text = "Grandchild amount" }] },
                 new DynamicTemplate { Slot = DynamicTextSlot.ReviewSummary, Parts = [new DynamicTextPart { Kind = DynamicPartKind.Value,
                    Source = new DynamicSource { Kind = DynamicSourceKind.Variable, Key = externalVariable.Id } }] }] }
        };
        var published = new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published, DisplayName = "Published preserved",
            CreatedUtc = DateTime.UtcNow, PublishedUtc = DateTime.UtcNow, Categories = [new Category { Name = "Published category", SortOrder = 1,
                Elements = [new Element { Kind = ElementKind.Content, Label = "Published instructions", SortOrder = 1 }] }] };
        var draft = new ApplicationVersion { VersionNumber = 2, Status = VersionStatus.Draft, DisplayName = "Deletion draft", CreatedUtc = DateTime.UtcNow,
            Categories = [sourceCategory, retainedCategory], VisibilityConditions = [condition], CompositionJson = composition.ToJson() };
        var app = new QuestionAppBuilder.Domain.Entities.Application { Name = "Deletion preview synthetic", ApplicationTypeId = 1,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, Versions = [published, draft] };
        db.Applications.Add(app);
        await db.SaveChangesAsync();
        app.CurrentVersionId = published.Id;
        await db.SaveChangesAsync();
        return new(app.Id, draft.Id, published.Id, sourceCategory.Id, root.Id, child.Id, grandchild.Id, other.Id, condition.Id,
            retainedCategory.Id, banner.Id, sourceCategory.LogicalKey, root.LogicalKey, grandchild.LogicalKey, other.LogicalKey, draft.RevisionNonce.ToByteArray());
    }

    private static async Task<string> SnapshotAsync(SqlServerDatabase database)
    {
        await using var db = database.CreateContext();
        return JsonSerializer.Serialize(new
        {
            Versions = await db.ApplicationVersions.OrderBy(version => version.Id).Select(version => new
                { version.Id, version.Status, version.RevisionNonce, version.CompositionJson, version.DisplayName }).ToArrayAsync(),
            Categories = await db.Categories.OrderBy(category => category.Id).Select(category => new { category.Id, category.VisibilityConditionId }).ToArrayAsync(),
            Elements = await db.Elements.OrderBy(element => element.Id).Select(element => new { element.Id, element.ParentOptionId, element.VisibilityConditionId }).ToArrayAsync(),
            Options = await db.QuestionOptions.OrderBy(option => option.Id).Select(option => option.Id).ToArrayAsync(),
            Rules = await db.ScoringRules.OrderBy(rule => rule.Id).Select(rule => rule.Id).ToArrayAsync(),
            Conditions = await db.VisibilityConditions.OrderBy(condition => condition.Id).Select(condition => condition.Id).ToArrayAsync()
        });
    }

    private static async Task<string> PublishedSnapshotAsync(SqlServerDatabase database, int versionId)
    {
        await using var db = database.CreateContext();
        var version = await db.ApplicationVersions.Include(item => item.Categories).ThenInclude(category => category.Elements)
            .SingleAsync(item => item.Id == versionId);
        return JsonSerializer.Serialize(new { version.Id, version.Status, version.RevisionNonce, version.DisplayName, version.CompositionJson,
            Categories = version.Categories.Select(category => new { category.Id, category.Name,
                Elements = category.Elements.Select(element => new { element.Id, element.Label, element.LogicalKey }) }) });
    }

    private sealed record Seed(int ApplicationId, int VersionId, int PublishedVersionId, int SourceCategoryId, int RootId,
        int ChildId, int GrandchildId, int OtherOptionId, int ConditionId, int RetainedCategoryId, int RetainedContentId,
        Guid SourceCategoryKey, Guid RootKey, Guid GrandchildKey, Guid OtherOptionKey, byte[] Revision);

    private sealed class DatabaseFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(database.CreateContext());
    }

    private sealed class PausedFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public async Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return database.CreateContext();
        }
    }

    private sealed class TestActor : ITrustedActorContext
    {
        public string GetRequiredActorKey() => "designer:deletion-tests";
    }

    private sealed class InterceptedFactory(SqlServerDatabase database, IInterceptor interceptor) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext(interceptor);
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(database.CreateContext(interceptor));
    }

    private sealed class BeforeSaveInterceptor(Func<Task> beforeSave) : SaveChangesInterceptor
    {
        public int Calls { get; private set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Calls++;
            await beforeSave();
            return result;
        }
    }
}

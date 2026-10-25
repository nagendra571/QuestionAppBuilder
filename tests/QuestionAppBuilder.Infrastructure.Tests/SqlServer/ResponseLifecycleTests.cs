using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Responses;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Composition;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Infrastructure.Persistence;
using QuestionAppBuilder.Infrastructure.Responses;

namespace QuestionAppBuilder.Infrastructure.Tests.SqlServer;

public class ResponseLifecycleTests
{
    [SqlServerFact]
    public async Task Save_then_new_context_resume_preserves_answers()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, respondent) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var questionId = db.QuestionId;
        var saved = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [questionId] = new AnswerValue("saved text", null, null) }));
        var (resumedService, _) = db.CreateService("owner-1");

        var resumed = await resumedService.GetAsync(started.Id);

        resumed.Id.Should().Be(started.Id);
        resumed.ApplicationVersionId.Should().Be(started.ApplicationVersionId);
        resumed.Answers[questionId].Text.Should().Be("saved text");
        resumed.Revision.Should().Equal(saved.Revision);
    }

    [SqlServerFact]
    public async Task Another_owner_gets_not_found_for_read_and_write()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var (otherOwner, _) = db.CreateService("owner-2");

        Func<Task> read = () => otherOwner.GetAsync(started.Id);
        Func<Task> write = () => otherOwner.SaveAsync(new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue>()));

        await read.Should().ThrowAsync<NotFoundException>();
        await write.Should().ThrowAsync<NotFoundException>();
    }

    [SqlServerFact]
    public async Task Email_grant_is_scoped_to_its_single_response_and_cannot_enumerate_owner_responses()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (owner, _) = db.CreateService("email:shared-owner");
        var grantedResponse = await owner.StartAsync(1);
        var otherResponse = await owner.StartAsync(1);
        var accessTokenId = Guid.NewGuid();
        await using (var context = db.CreateContext())
        {
            context.ResponseAccessTokens.Add(new ResponseAccessToken
            {
                Id = accessTokenId, TokenDigest = new byte[32], ApplicationId = 1,
                ApplicationVersionId = grantedResponse.ApplicationVersionId, ResponseId = grantedResponse.Id,
                OwnerKey = "email:shared-owner", VerifiedEmail = "person@example.test", CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(30), ConsumedUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }
        var grantIdentity = new RespondentIdentity("email:shared-owner", grantedResponse.Id, grantedResponse.ApplicationVersionId, accessTokenId);
        var (grantedService, respondent) = db.CreateService(grantIdentity);
        var (otherOwnerResponse, _) = db.CreateService("email:shared-owner");
        var visible = await grantedService.GetAsync(grantedResponse.Id);
        visible.Id.Should().Be(grantedResponse.Id);
        (await grantedService.ListMineAsync(50)).Select(item => item.Id).Should().Equal(grantedResponse.Id);

        Func<Task> readOther = () => grantedService.GetAsync(otherResponse.Id);
        Func<Task> saveOther = () => grantedService.SaveAsync(new SaveResponseRequest(otherResponse.Id, otherResponse.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new("stolen", null, null) }));
        Func<Task> submitOther = () => db.CreateSubmissionCoordinator(respondent)
            .SubmitAsync(new SubmitResponseRequest(otherResponse.Id, otherResponse.Revision, Guid.NewGuid()));
        await readOther.Should().ThrowAsync<NotFoundException>();
        await saveOther.Should().ThrowAsync<NotFoundException>();
        await submitOther.Should().ThrowAsync<NotFoundException>();
        (await otherOwnerResponse.ListMineAsync(50)).Should().HaveCount(2);
    }

    [SqlServerFact]
    public async Task Two_saves_same_revision_one_conflicts()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var questionId = db.QuestionId;
        var request1 = new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue> { [questionId] = new("one", null, null) });
        var request2 = new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue> { [questionId] = new("two", null, null) });
        await service.SaveAsync(request1);

        Func<Task> stale = () => service.SaveAsync(request2);

        await stale.Should().ThrowAsync<ResponseConflictException>();
        (await service.GetAsync(started.Id)).Answers[questionId].Text.Should().Be("one");
    }

    [SqlServerFact]
    public async Task Publish_new_version_preserves_existing_response_version()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        await using (var context = db.CreateContext())
        {
            var v2 = new ApplicationVersion { ApplicationId = 1, VersionNumber = 2, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow, PublishedUtc = DateTime.UtcNow, DisplayName = "v2" };
            context.ApplicationVersions.Add(v2);
            await context.SaveChangesAsync();
            var app = await context.Applications.SingleAsync();
            app.CurrentVersionId = v2.Id;
            await context.SaveChangesAsync();
        }

        var resumed = await service.GetAsync(started.Id);

        resumed.ApplicationVersionId.Should().Be(started.ApplicationVersionId);
        resumed.Definition.Version.VersionNumber.Should().Be(1);
    }

    [SqlServerFact]
    public async Task Receipt_reloads_frozen_submitted_application_after_draft_is_cleared()
    {
        await using var db = await TestDatabase.CreateAsync(submittedApplication: true,
            resultVisibility: ResultVisibility.Total, submittedApplicationVisibility: SubmittedApplicationVisibility.WithoutScores);
        var (responses, identity) = db.CreateService("receipt-owner");
        var started = await responses.StartAsync(1);
        var saved = await responses.SaveAsync(new(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new("frozen answer", null, null) }));
        var requestId = Guid.NewGuid();

        var submitted = await db.CreateSubmissionCoordinator(identity).SubmitAsync(
            new(started.Id, saved.Revision, requestId));
        var replayed = await db.CreateSubmissionCoordinator(identity).SubmitAsync(
            new(started.Id, saved.Revision, requestId));

        submitted.SubmittedApplication.Should().BeNull("Results shown controls the immediate receipt");
        replayed.SubmittedApplication.Should().BeNull();
        submitted.Total.Should().Be(0);
        submitted.SubmittedApplication.Should().BeNull();
        var submittedView = await db.CreateSubmissionCoordinator(identity).GetSubmittedApplicationAsync(started.Id);
        submittedView.SubmittedApplication.Should().NotBeNull();
        submittedView.Total.Should().BeNull("the separate reopening setting hides scores");
        submittedView.Categories.Should().BeNull();
        submittedView.SubmittedApplication!.Categories.Select(category => category.Name)
            .Should().Contain("Content only").And.NotContain("Hidden branch");
        submittedView.SubmittedApplication.Categories.Single(category => category.Name == "Content only")
            .Elements.Should().ContainSingle(element => element.Label == "Receipt introduction");
        submittedView.SubmittedApplication.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).Select(element => element.Question!)
            .Should().NotContain(question => question.Id == db.HiddenQuestionId);
        submittedView.SubmittedApplication.Categories.SelectMany(category => category.Elements)
            .Should().Contain(element => element.Label == "Visible conditional introduction")
            .And.NotContain(element => element.Label == "Conditional introduction");

        await using (var context = db.CreateContext())
        {
            (await context.ApplicationVersions.Select(version => version.SubmittedApplicationVisibility).SingleAsync())
                .Should().Be(SubmittedApplicationVisibility.WithoutScores);
            var response = await context.ResponseDrafts.Include(item => item.Answers).SingleAsync(item => item.Id == started.Id);
            response.Answers.Should().BeEmpty();
            var persisted = await context.Submissions.Include(item => item.Answers)
                .Include(item => item.QuestionStates).SingleAsync(item => item.ResponseId == started.Id);
            persisted.Answers.Should().ContainSingle(answer => answer.QuestionId == db.QuestionId && answer.TextValue == "frozen answer");
            persisted.QuestionStates.Should().Contain(state => state.QuestionId == db.HiddenQuestionId && !state.IsApplicable);
            persisted.CompositionSnapshotJson.Should().Contain("VisibleCategoryIds").And.Contain("VisibleElementIds");
            persisted.CompositionSnapshotJson.Should().NotContain("CombinedScoreRules").And.NotContain("FailedValidationRules");
        }
        await using (var context = db.CreateContext())
        {
            var nextVersion = new ApplicationVersion
            {
                ApplicationId = 1, VersionNumber = 2, Status = VersionStatus.Published,
                CreatedUtc = DateTime.UtcNow, PublishedUtc = DateTime.UtcNow, DisplayName = "Changed questionnaire",
                Categories = { new Category { Name = "Changed category", SortOrder = 1 } }
            };
            context.ApplicationVersions.Add(nextVersion);
            await context.SaveChangesAsync();
            var application = await context.Applications.SingleAsync(item => item.Id == 1);
            application.CurrentVersionId = nextVersion.Id;
            var submittedVersion = await context.ApplicationVersions.Include(item => item.VisibilityConditions)
                .ThenInclude(condition => condition.Predicates).SingleAsync(item => item.Id == started.ApplicationVersionId);
            submittedVersion.VisibilityConditions.Single(condition => condition.Predicates.Single().Operator == ConditionOperator.Unanswered)
                .Predicates.Single().Operator = ConditionOperator.Answered;
            await context.SaveChangesAsync();
        }

        var (freshResponses, freshIdentity) = db.CreateService("receipt-owner");
        var reopened = await db.CreateSubmissionCoordinator(freshIdentity).GetSubmittedApplicationAsync(started.Id);
        reopened.SubmittedApplication.Should().BeEquivalentTo(submittedView.SubmittedApplication);
        reopened.SubmittedApplication!.Title.Should().Be("v1");
        reopened.SubmittedApplication.Categories.Single(category => category.Name == "Basics")
            .Elements.Where(element => element.Question is not null).Select(element => element.Question!)
            .Should().ContainSingle(question => question.AnswerText == "frozen answer");
        var resumedSubmitted = await freshResponses.GetAsync(started.Id);
        resumedSubmitted.Status.Should().Be(ResponseStatus.Submitted);
        resumedSubmitted.Definition.Version.SubmittedApplicationVisibility.Should().Be(SubmittedApplicationVisibility.WithoutScores);
        reopened.SubmittedApplication.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).Select(element => element.Question!.PointsEarned)
            .Should().OnlyContain(points => points == null);
        reopened.SubmittedApplication.Categories.SelectMany(category => category.Awards).Should().BeEmpty();
        reopened.SubmittedApplication.Categories.SelectMany(category => category.CalculatedValues).Should().BeEmpty();

        await using (var context = db.CreateContext())
        {
            var persisted = await context.Submissions.SingleAsync(item => item.ResponseId == started.Id);
            var snapshot = JsonNode.Parse(persisted.CompositionSnapshotJson!)!.AsObject();
            snapshot["VisibleElementIds"] = new JsonArray();
            persisted.CompositionSnapshotJson = snapshot.ToJsonString();
            await context.SaveChangesAsync();
        }
        var explicitEmpty = await db.CreateSubmissionCoordinator(freshIdentity).GetSubmittedApplicationAsync(started.Id);
        explicitEmpty.SubmittedApplication!.Categories.Single(category => category.Name == "Content only").Elements.Should().BeEmpty();

        await using (var context = db.CreateContext())
        {
            var persisted = await context.Submissions.SingleAsync(item => item.ResponseId == started.Id);
            var snapshot = JsonNode.Parse(persisted.CompositionSnapshotJson!)!.AsObject();
            snapshot["VisibleElementIds"] = null;
            persisted.CompositionSnapshotJson = snapshot.ToJsonString();
            await context.SaveChangesAsync();
        }
        var nullLegacy = await db.CreateSubmissionCoordinator(freshIdentity).GetSubmittedApplicationAsync(started.Id);
        nullLegacy.SubmittedApplication!.Categories.Single(category => category.Name == "Content only")
            .Elements.Select(element => element.Label).Should().Contain("Receipt introduction").And.Contain("Conditional introduction")
            .And.Contain("Visible conditional introduction");

        await using (var context = db.CreateContext())
        {
            var persisted = await context.Submissions.SingleAsync(item => item.ResponseId == started.Id);
            var snapshot = JsonNode.Parse(persisted.CompositionSnapshotJson!)!.AsObject();
            snapshot.Remove("VisibleElementIds");
            persisted.CompositionSnapshotJson = snapshot.ToJsonString();
            await context.SaveChangesAsync();
        }
        var absentLegacy = await db.CreateSubmissionCoordinator(freshIdentity).GetSubmittedApplicationAsync(started.Id);
        absentLegacy.SubmittedApplication!.Categories.Single(category => category.Name == "Content only")
            .Elements.Select(element => element.Label).Should().Contain("Receipt introduction").And.Contain("Conditional introduction")
            .And.Contain("Visible conditional introduction");
    }

    [SqlServerFact]
    public async Task With_results_receipt_keeps_zero_total_without_zero_detail_scores()
    {
        await using var db = await TestDatabase.CreateAsync(resultVisibility: ResultVisibility.Total,
            submittedApplicationVisibility: SubmittedApplicationVisibility.WithScores);
        var (responses, identity) = db.CreateService("results-owner");
        var started = await responses.StartAsync(1);
        var saved = await responses.SaveAsync(new(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new("no points", null, null) }));

        await db.CreateSubmissionCoordinator(identity).SubmitAsync(
            new(started.Id, saved.Revision, Guid.NewGuid()));
        var receipt = await db.CreateSubmissionCoordinator(identity).GetSubmittedApplicationAsync(started.Id);

        receipt.Total.Should().Be(0);
        receipt.SubmittedApplication.Should().NotBeNull();
        receipt.SubmittedApplication!.Categories.SelectMany(category => category.Elements)
            .Where(element => element.Question is not null).Select(element => element.Question!.PointsEarned)
            .Should().OnlyContain(points => points == null);
        receipt.SubmittedApplication.Categories.SelectMany(category => category.Awards).Should().BeEmpty();
        receipt.SubmittedApplication.Categories.SelectMany(category => category.CalculatedValues).Should().BeEmpty();
    }

    [SqlServerFact]
    public async Task With_results_receipt_projects_positive_question_category_and_combined_awards_from_persisted_submission()
    {
        await using var db = await TestDatabase.CreateAsync(answerType: AnswerType.Number,
            submittedApplication: true, resultVisibility: ResultVisibility.ConfirmationAndAnswerReview,
            submittedApplicationVisibility: SubmittedApplicationVisibility.WithScores, positiveResults: true);
        await using (var context = db.CreateContext())
        {
            (await context.ApplicationVersions.Select(version => version.SubmittedApplicationVisibility).SingleAsync())
                .Should().Be(SubmittedApplicationVisibility.WithScores);
        }
        var (responses, identity) = db.CreateService("positive-results-owner");
        var started = await responses.StartAsync(1);
        (await responses.GetAsync(started.Id)).Definition.Version.SubmittedApplicationVisibility
            .Should().Be(SubmittedApplicationVisibility.WithScores);
        var saved = await responses.SaveAsync(new(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, 5, null) }));
        var coordinator = db.CreateSubmissionCoordinator(identity);
        var submitted = await coordinator.SubmitAsync(new(started.Id, saved.Revision, Guid.NewGuid()));

        submitted.Total.Should().Be(2);
        var category = submitted.SubmittedApplication!.Categories.Single(item => item.Name == "Basics");
        category.PointsEarned.Should().Be(2);
        category.Elements.Single(element => element.Question is not null).Question!.PointsEarned.Should().Be(1);
        category.Awards.Should().ContainSingle().Which.Should().Match<SubmittedAward>(award => award.Name == "Combined award" && award.Points == 1);
        category.CalculatedValues.Should().ContainSingle().Which.Value.Should().Be(7);
        category.Groups.Should().ContainSingle().Which.Should().Match<SubmittedGroup>(group => group.TableLayout
            && group.Rows.Count == 1 && group.Rows[0].Element.Question!.AnswerText == "5"
            && group.Rows[0].Cells.Single() == "Primary");
        category.Elements.Single(element => element.Question is not null).Question!.PointsEarned.Should().NotBe(0);

        var reloaded = await db.CreateSubmissionCoordinator(db.CreateService("positive-results-owner").Identity)
            .GetSubmittedApplicationAsync(started.Id);
        reloaded.SubmittedApplication.Should().BeEquivalentTo(submitted.SubmittedApplication);
    }

    [SqlServerFact]
    public async Task Confirmation_only_receipt_can_reopen_with_scores_when_submitted_application_setting_allows_them()
    {
        await using var db = await TestDatabase.CreateAsync(answerType: AnswerType.Number,
            submittedApplication: true, resultVisibility: ResultVisibility.ConfirmationOnly,
            submittedApplicationVisibility: SubmittedApplicationVisibility.WithScores, positiveResults: true);
        var (responses, identity) = db.CreateService("reopen-results-owner");
        var started = await responses.StartAsync(1);
        var saved = await responses.SaveAsync(new(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, 5, null) }));
        var coordinator = db.CreateSubmissionCoordinator(identity);

        var confirmation = await coordinator.SubmitAsync(new(started.Id, saved.Revision, Guid.NewGuid()));
        var reopened = await coordinator.GetSubmittedApplicationAsync(started.Id);

        confirmation.Total.Should().BeNull();
        confirmation.Categories.Should().BeNull();
        confirmation.SubmittedApplication.Should().BeNull();
        reopened.Total.Should().Be(2);
        reopened.SubmittedApplication.Should().NotBeNull();
        reopened.SubmittedApplication!.Categories.Single(category => category.Name == "Basics")
            .PointsEarned.Should().Be(2);
    }

    [SqlServerFact]
    public async Task Confirmation_and_answer_review_receipt_keeps_scores_when_reopening_hides_them()
    {
        await using var db = await TestDatabase.CreateAsync(submittedApplication: true,
            resultVisibility: ResultVisibility.ConfirmationAndAnswerReview,
            submittedApplicationVisibility: SubmittedApplicationVisibility.WithoutScores);
        var (responses, identity) = db.CreateService("review-owner");
        var started = await responses.StartAsync(1);
        var saved = await responses.SaveAsync(new(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new("answer", null, null) }));
        var coordinator = db.CreateSubmissionCoordinator(identity);

        var immediate = await coordinator.SubmitAsync(new(started.Id, saved.Revision, Guid.NewGuid()));
        var reopened = await coordinator.GetSubmittedApplicationAsync(started.Id);

        immediate.Total.Should().Be(0);
        immediate.SubmittedApplication.Should().NotBeNull();
        reopened.Total.Should().BeNull();
        reopened.Categories.Should().BeNull();
        reopened.SubmittedApplication.Should().NotBeNull();
        reopened.SubmittedApplication!.Categories.Single(category => category.Name == "Basics")
            .Elements.Single(element => element.Question is not null).Question!.PointsEarned.Should().BeNull();
    }

    [SqlServerFact]
    public async Task Receipt_access_requires_the_owner_and_the_matching_consumed_response_grant()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (owner, ownerIdentity) = db.CreateService("receipt-owner");
        var first = await owner.StartAsync(1);
        var second = await owner.StartAsync(1);
        foreach (var response in new[] { first, second })
        {
            var complete = await owner.SaveAsync(new(response.Id, response.Revision,
                new Dictionary<int, AnswerValue> { [db.QuestionId] = new($"answer {response.Id}", null, null) }));
            await db.CreateSubmissionCoordinator(ownerIdentity).SubmitAsync(new(response.Id, complete.Revision, Guid.NewGuid()));
        }

        var tokenId = Guid.NewGuid();
        await using (var context = db.CreateContext())
        {
            context.ResponseAccessTokens.Add(new ResponseAccessToken
            {
                Id = tokenId, TokenDigest = new byte[32], ApplicationId = 1,
                ApplicationVersionId = first.ApplicationVersionId, ResponseId = first.Id,
                OwnerKey = "receipt-owner", VerifiedEmail = "person@example.test", CreatedUtc = DateTime.UtcNow,
                ExpiresUtc = DateTime.UtcNow.AddMinutes(30), ConsumedUtc = DateTime.UtcNow
            });
            await context.SaveChangesAsync();
        }

        var grantIdentity = new RespondentIdentity("receipt-owner", first.Id, first.ApplicationVersionId, tokenId);
        var scoped = db.CreateSubmissionCoordinator(new TestRespondentContext(grantIdentity));
        (await scoped.GetReceiptAsync(first.Id)).ResponseId.Should().Be(first.Id);
        (await scoped.GetSubmittedApplicationAsync(first.Id)).SubmittedApplication.Should().NotBeNull();
        Func<Task> otherReceipt = () => scoped.GetReceiptAsync(second.Id);
        await otherReceipt.Should().ThrowAsync<NotFoundException>();
        Func<Task> otherSubmittedApplication = () => scoped.GetSubmittedApplicationAsync(second.Id);
        await otherSubmittedApplication.Should().ThrowAsync<NotFoundException>();

        var otherOwner = db.CreateSubmissionCoordinator(new TestRespondentContext(new RespondentIdentity("someone-else", null, null)));
        Func<Task> ownerPrivateReceipt = () => otherOwner.GetReceiptAsync(first.Id);
        await ownerPrivateReceipt.Should().ThrowAsync<NotFoundException>();
        Func<Task> ownerPrivateSubmittedApplication = () => otherOwner.GetSubmittedApplicationAsync(first.Id);
        await ownerPrivateSubmittedApplication.Should().ThrowAsync<NotFoundException>();
    }

    [SqlServerFact]
    public async Task Closed_submissions_still_allow_owned_draft_save()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        await using (var context = db.CreateContext())
        {
            var app = await context.Applications.SingleAsync();
            app.AcceptSubmissions = false;
            await context.SaveChangesAsync();
        }

        var saved = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new("still saving", null, null) }));

        saved.Answers[db.QuestionId].Text.Should().Be("still saving");
    }

    [SqlServerFact]
    public async Task Invalid_option_is_rejected_but_missing_required_saves()
    {
        await using var db = await TestDatabase.CreateAsync(choiceQuestion: true);
        var (service, respondent) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var (optionId, _) = db.ChoiceIds;

        var incomplete = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision, new Dictionary<int, AnswerValue>()));
        incomplete.Answers.Should().BeEmpty();
        Func<Task> invalid = () => service.SaveAsync(new SaveResponseRequest(started.Id, incomplete.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, null, [int.MaxValue]) }));

        await invalid.Should().ThrowAsync<AnswerValidationException>();
        var good = await service.SaveAsync(new SaveResponseRequest(started.Id, incomplete.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, null, [optionId]) }));
        good.Answers[db.QuestionId].SelectedOptionIds.Should().ContainSingle().Which.Should().Be(optionId);

        await using var freshContext = db.CreateContext();
        var persisted = await freshContext.ResponseAnswers.Include(answer => answer.SelectedOptions)
            .SingleAsync(answer => answer.ResponseDraftId == started.Id && answer.QuestionId == db.QuestionId);
        persisted.SelectedOptions.Select(option => option.QuestionOptionId).Should().ContainSingle().Which.Should().Be(optionId);

        var receipt = await db.CreateSubmissionCoordinator(respondent).SubmitAsync(
            new SubmitResponseRequest(started.Id, good.Revision, Guid.NewGuid()));
        await using var submissionContext = db.CreateContext();
        var submittedAnswer = await submissionContext.Answers.Include(answer => answer.SelectedOptions)
            .SingleAsync(answer => answer.SubmissionId == receipt.SubmissionId && answer.QuestionId == db.QuestionId);
        submittedAnswer.SelectedOptions.Select(option => option.QuestionOptionId).Should().ContainSingle().Which.Should().Be(optionId);
    }

    [SqlServerFact]
    public async Task Empty_field_replaces_prior_value()
    {
        await using var db = await TestDatabase.CreateAsync();
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        var questionId = db.QuestionId;
        var first = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [questionId] = new("old value", null, null) }));

        var cleared = await service.SaveAsync(new SaveResponseRequest(started.Id, first.Revision,
            new Dictionary<int, AnswerValue> { [questionId] = new(null, null, null) }));

        cleared.Answers.Should().NotContainKey(questionId);
    }

    [SqlServerFact]
    public async Task Numeric_input_requiring_decimal_truncation_is_rejected()
    {
        await using var db = await TestDatabase.CreateAsync(answerType: AnswerType.Number);
        var (service, _) = db.CreateService("owner-1");
        var started = await service.StartAsync(1);
        Func<Task> lossy = () => service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, 1.23456m, null) }));

        await lossy.Should().ThrowAsync<AnswerValidationException>();
        var exact = await service.SaveAsync(new SaveResponseRequest(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new(null, 1.2345m, null) }));
        exact.Answers[db.QuestionId].Number.Should().Be(1.2345m);
    }

    [SqlServerFact]
    public async Task Documents_cannot_be_forged_by_answer_saves_and_survive_resume_and_submission()
    {
        await using var db = await TestDatabase.CreateAsync(answerType: AnswerType.UploadDocument);
        var (responses, identity) = db.CreateService("document-owner");
        var documents = db.CreateDocuments(responses);
        var started = await responses.StartAsync(1);
        var fake = await responses.SaveAsync(new(started.Id, started.Revision,
            new Dictionary<int, AnswerValue> { [db.QuestionId] = new("fake.pdf", null, null) }));
        fake.Answers.Should().BeEmpty();
        Func<Task> incomplete = () => db.CreateSubmissionCoordinator(identity).SubmitAsync(new(fake.Id, fake.Revision, Guid.NewGuid()));
        await incomplete.Should().ThrowAsync<AnswerValidationException>();
        var bytes = System.Text.Encoding.UTF8.GetBytes("%PDF-1.7\nTest document");
        var uploaded = await documents.UploadAsync(fake.Id, db.QuestionId, fake.Revision, "../../budget.pdf", new MemoryStream(bytes));
        uploaded.Answers[db.QuestionId].Text.Should().Be("budget.pdf");
        var resumed = await responses.SaveAsync(new(uploaded.Id, uploaded.Revision, new Dictionary<int, AnswerValue>()));
        resumed.Answers[db.QuestionId].Text.Should().Be("budget.pdf");
        (await documents.DownloadAsync(resumed.Id, db.QuestionId))!.Content.Should().Equal(bytes);
        var (other, _) = db.CreateService("another-owner");
        Func<Task> steal = () => db.CreateDocuments(other).DownloadAsync(resumed.Id, db.QuestionId);
        await steal.Should().ThrowAsync<NotFoundException>();
        Func<Task> stale = () => documents.RemoveAsync(resumed.Id, db.QuestionId, fake.Revision);
        await stale.Should().ThrowAsync<ResponseConflictException>();
        var receipt = await db.CreateSubmissionCoordinator(identity).SubmitAsync(new(resumed.Id, resumed.Revision, Guid.NewGuid()));
        (await documents.DownloadSubmissionAsync(receipt.SubmissionId, db.QuestionId))!.Content.Should().Equal(bytes);
        var submitted = await responses.GetAsync(resumed.Id);
        Func<Task> editSubmitted = () => documents.RemoveAsync(submitted.Id, db.QuestionId, submitted.Revision);
        await editSubmitted.Should().ThrowAsync<ResponseConflictException>();
    }

    [SqlServerFact]
    public async Task Document_replacement_removal_and_invalid_content_preserve_consistent_answers()
    {
        await using var db = await TestDatabase.CreateAsync(answerType: AnswerType.UploadDocument);
        var (responses, _) = db.CreateService("document-owner");
        var documents = db.CreateDocuments(responses);
        var response = await responses.StartAsync(1);
        foreach (var file in new[] { ("bad.exe", "executable"), ("fake.pdf", "not PDF"), ("empty.txt", ""), ("macro.docx", "not a zip") })
        {
            Func<Task> invalid = () => documents.UploadAsync(response.Id, db.QuestionId, response.Revision,
                file.Item1, new MemoryStream(System.Text.Encoding.UTF8.GetBytes(file.Item2)));
            await invalid.Should().ThrowAsync<ArgumentException>();
        }
        Func<Task> oversized = () => documents.UploadAsync(response.Id, db.QuestionId, response.Revision,
            "large.txt", new MemoryStream(new byte[IResponseDocumentService.MaximumBytes + 1]));
        await oversized.Should().ThrowAsync<ArgumentException>();
        var first = await documents.UploadAsync(response.Id, db.QuestionId, response.Revision, "first.txt", new MemoryStream("First document"u8.ToArray()));
        var replaced = await documents.UploadAsync(response.Id, db.QuestionId, first.Revision, "second.txt", new MemoryStream("Replacement"u8.ToArray()));
        replaced.Answers[db.QuestionId].Text.Should().Be("second.txt");
        (await documents.DownloadAsync(response.Id, db.QuestionId))!.Content.Should().Equal("Replacement"u8.ToArray());
        using var wordBuffer = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(wordBuffer, System.IO.Compression.ZipArchiveMode.Create, true))
        {
            using (var writer = new StreamWriter(archive.CreateEntry("[Content_Types].xml").Open()))
                writer.Write("<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\" />");
            using (var writer = new StreamWriter(archive.CreateEntry("word/document.xml").Open()))
                writer.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body /></w:document>");
        }
        var word = await documents.UploadAsync(response.Id, db.QuestionId, replaced.Revision, "document.docx", new MemoryStream(wordBuffer.ToArray()));
        word.Answers[db.QuestionId].Text.Should().Be("document.docx");
        var removed = await documents.RemoveAsync(response.Id, db.QuestionId, word.Revision);
        removed.Answers.Should().BeEmpty();
        (await documents.DownloadAsync(response.Id, db.QuestionId)).Should().BeNull();
    }

    [SqlServerFact]
    public async Task Hidden_document_follow_ups_are_restored_but_excluded_from_submission_downloads()
    {
        await using var db = await TestDatabase.CreateAsync(answerType: AnswerType.UploadDocument, followUp: true);
        var (responses, identity) = db.CreateService("document-owner");
        var documents = db.CreateDocuments(responses);
        var response = await responses.StartAsync(1);
        var parent = response.Definition.Categories.Single().Elements.Single(e => e.Question!.AnswerType == AnswerType.RadioGroup).Question!;
        Func<Task> hiddenUpload = () => documents.UploadAsync(response.Id, db.QuestionId, response.Revision, "notes.txt", new MemoryStream("Notes"u8.ToArray()));
        await hiddenUpload.Should().ThrowAsync<NotFoundException>();
        var shown = await responses.SaveAsync(new(response.Id, response.Revision,
            new Dictionary<int, AnswerValue> { [parent.Id] = new(null, null, [parent.Options.Single().Id]) }));
        var uploaded = await documents.UploadAsync(response.Id, db.QuestionId, shown.Revision, "notes.txt", new MemoryStream("Notes"u8.ToArray()));
        var hidden = await responses.SaveAsync(new(response.Id, uploaded.Revision, new Dictionary<int, AnswerValue>()));
        hidden.Answers[db.QuestionId].Text.Should().Be("notes.txt");
        var receipt = await db.CreateSubmissionCoordinator(identity).SubmitAsync(new(hidden.Id, hidden.Revision, Guid.NewGuid()));
        (await documents.DownloadSubmissionAsync(receipt.SubmissionId, db.QuestionId)).Should().BeNull();
        (await documents.DownloadAsync(hidden.Id, db.QuestionId)).Should().BeNull();
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqlServerDatabase _sql;
        private readonly DefinitionEvaluator _evaluator = new();
        private TestDatabase(SqlServerDatabase sql) => _sql = sql;
        public int QuestionId { get; private set; }
        public int HiddenQuestionId { get; private set; }
        public (int First, int Second) ChoiceIds { get; private set; }
        public QuestionAppBuilderDbContext CreateContext() => _sql.CreateContext();
        public (IResponseService Service, TestRespondentContext Identity) CreateService(string owner)
            => CreateService(new RespondentIdentity(owner, null, null));
        public (IResponseService Service, TestRespondentContext Identity) CreateService(RespondentIdentity identity)
        {
            var respondent = new TestRespondentContext(identity);
            return (new ResponseService(new DatabaseContextFactory(_sql), respondent), respondent);
        }
        public IResponseDocumentService CreateDocuments(IResponseService responses)
            => new ResponseDocumentService(new DatabaseContextFactory(_sql), responses, new DefinitionEvaluator());
        public ISubmissionCoordinator CreateSubmissionCoordinator(IRespondentContext identity)
            => new SubmissionCoordinator(new DatabaseContextFactory(_sql), identity, new DefinitionEvaluator());
        public static async Task<TestDatabase> CreateAsync(bool choiceQuestion = false, AnswerType answerType = AnswerType.Textbox, bool followUp = false,
            bool submittedApplication = false, ResultVisibility resultVisibility = ResultVisibility.Total,
            SubmittedApplicationVisibility submittedApplicationVisibility = SubmittedApplicationVisibility.WithoutScores, bool positiveResults = false)
        {
            var db = new TestDatabase(await SqlServerDatabase.CreateAsync());
            await using var context = db.CreateContext();
            await context.Database.MigrateAsync();
            var question = new Question { AnswerType = choiceQuestion ? AnswerType.Dropdown : answerType, IsRequired = true };
            if (positiveResults)
            {
                question.MinValue = 0;
                question.MaxValue = 10;
                question.Rules.Add(new ScoringRule { Family = RuleFamily.NumericRange, MinValue = 0, MaxValue = 10,
                    Points = 1, AwardMethod = AwardMethod.Fixed });
            }
            if (choiceQuestion)
            {
                question.Options.Add(new QuestionOption { Label = "One", Value = "one", SortOrder = 1 });
                question.Options.Add(new QuestionOption { Label = "Two", Value = "two", SortOrder = 2 });
            }
            var app = new QuestionAppBuilder.Domain.Entities.Application
            {
                Name = "Response lifecycle", ApplicationTypeId = 1, CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow,
                AcceptNewResponses = true, AcceptSubmissions = true,
                Versions = { new ApplicationVersion { VersionNumber = 1, Status = VersionStatus.Published, CreatedUtc = DateTime.UtcNow,
                    PublishedUtc = DateTime.UtcNow, DisplayName = "v1", ResultVisibility = resultVisibility,
                    SubmittedApplicationVisibility = submittedApplicationVisibility, Categories = { new Category { Name = "Basics", SortOrder = 1,
                        Elements = { new Element { Kind = ElementKind.Question, Label = "Answer", SortOrder = 1, Question = question } } } } } }
            };
            if (submittedApplication)
            {
                var version = app.Versions.Single();
                var contentCategory = new Category { Name = "Content only", SortOrder = 2,
                    Elements = { new Element { Kind = ElementKind.Content, Label = "Receipt introduction", SortOrder = 1 } } };
                version.Categories.Add(contentCategory);
                var hiddenQuestion = new Question { AnswerType = AnswerType.Textbox, IsRequired = false };
                var hiddenElement = new Element { Kind = ElementKind.Question, Label = "Hidden question", SortOrder = 1, Question = hiddenQuestion };
                var hiddenCategory = new Category { Name = "Hidden branch", SortOrder = 3,
                    Elements = { hiddenElement } };
                version.Categories.Add(hiddenCategory);
                var condition = new VisibilityCondition { Junction = ConditionJunction.All };
                condition.Predicates.Add(new VisibilityPredicate { SourceQuestion = question, Operator = ConditionOperator.Unanswered });
                contentCategory.Elements.Add(new Element { Kind = ElementKind.Content, Label = "Conditional introduction", SortOrder = 2,
                    VisibilityCondition = condition });
                hiddenCategory.VisibilityCondition = condition;
                version.VisibilityConditions.Add(condition);
                var answeredCondition = new VisibilityCondition { Junction = ConditionJunction.All };
                answeredCondition.Predicates.Add(new VisibilityPredicate { SourceQuestion = question, Operator = ConditionOperator.Answered });
                contentCategory.Elements.Add(new Element { Kind = ElementKind.Content, Label = "Visible conditional introduction", SortOrder = 3,
                    VisibilityCondition = answeredCondition });
                version.VisibilityConditions.Add(answeredCondition);
                if (positiveResults)
                {
                    var basics = version.Categories.Single(category => category.Name == "Basics");
                    version.CompositionJson = new CompositionDefinition
                    {
                        Groups = [new QuestionGroup { Name = "Summary", CategoryKey = basics.LogicalKey,
                            Columns = ["Details"], Rows = [new GroupRow { QuestionKey = basics.Elements.Single().LogicalKey, Cells = ["Primary"] }] }],
                        Calculations = [new CalculatedField { Name = "Combined value", CategoryKey = basics.LogicalKey,
                            Operation = CalculationOperation.Sum, Sources = [new ValueSource { Kind = SourceKind.Constant, Value = 4 },
                                new ValueSource { Kind = SourceKind.Constant, Value = 3 }] }],
                        Rules = [new CompositionRule { Name = "Combined award", CategoryKey = basics.LogicalKey,
                            TargetQuestionKey = basics.Elements.Single().LogicalKey, Kind = RulePurpose.Score, Points = 1,
                            Checks = [new ValueCheck { Left = new ValueSource { Kind = SourceKind.Constant, Value = 1 },
                                Comparison = Comparison.Equal, Right = new ValueSource { Kind = SourceKind.Constant, Value = 1 } }] }]
                    }.ToJson();
                }
            }
            if (followUp)
            {
                var category = app.Versions.Single().Categories.Single();
                var option = new QuestionOption { Label = "Other", Value = "other" };
                category.Elements.Single().ParentOption = option;
                category.Elements.Insert(0, new Element { Kind = ElementKind.Question, Label = "Type", SortOrder = 0,
                    Question = new Question { AnswerType = AnswerType.RadioGroup, Options = { option } } });
            }
            context.Applications.Add(app);
            await context.SaveChangesAsync();
            app.CurrentVersionId = app.Versions.Single().Id;
            await context.SaveChangesAsync();
            db.QuestionId = question.Id;
            db.HiddenQuestionId = submittedApplication
                ? app.Versions.Single().Categories.Single(category => category.Name == "Hidden branch").Elements.Single().Question!.Id
                : 0;
            if (choiceQuestion) db.ChoiceIds = (question.Options[0].Id, question.Options[1].Id);
            return db;
        }
        public ValueTask DisposeAsync() => _sql.DisposeAsync();
    }

    private sealed class TestRespondentContext(RespondentIdentity identity) : IRespondentContext
    {
        public RespondentIdentity GetRequiredIdentity() => identity;
    }

    private sealed class DatabaseContextFactory(SqlServerDatabase database) : IDbContextFactory<QuestionAppBuilderDbContext>
    {
        public QuestionAppBuilderDbContext CreateDbContext() => database.CreateContext();
        public Task<QuestionAppBuilderDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) => Task.FromResult(database.CreateContext());
    }
}

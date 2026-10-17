using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Interfaces;

namespace QuestionAppBuilder.Application.Services;

/// <summary>Legacy evaluator adapter retained for compatibility; production hosts must use ISubmissionCoordinator.</summary>
[Obsolete("Use ISubmissionCoordinator with an owned, persisted response.")]
public sealed class SubmissionService : ISubmissionService
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly ISubmissionRepository _submissionRepository;
    private readonly IDefinitionEvaluator _evaluator;

    public SubmissionService(
        IApplicationRepository applicationRepository,
        ISubmissionRepository submissionRepository,
        IDefinitionEvaluator evaluator)
    {
        ArgumentNullException.ThrowIfNull(applicationRepository);
        ArgumentNullException.ThrowIfNull(submissionRepository);
        ArgumentNullException.ThrowIfNull(evaluator);

        _applicationRepository = applicationRepository;
        _submissionRepository = submissionRepository;
        _evaluator = evaluator;
    }

    public async Task<Submission> SubmitAsync(SubmitRequest request, CancellationToken ct = default)
    {
        var graph = await _applicationRepository.GetVersionGraphAsync(request.ApplicationVersionId, ct);
        if (graph.Version.Status != VersionStatus.Published || graph.Version.ApplicationId != request.ApplicationId)
        {
            throw new NotFoundException(
                $"Published application version {request.ApplicationVersionId} of application {request.ApplicationId} was not found.");
        }

        var evaluation = _evaluator.Evaluate(graph, request.Answers, requireComplete: true);
        if (evaluation.DefinitionIssues.Count > 0)
            throw new DefinitionValidationException(string.Join(" ", evaluation.DefinitionIssues.Select(issue => issue.Message)));
        if (evaluation.AnswerIssues.Count > 0)
        {
            var invalidQuestionIds = evaluation.AnswerIssues.Where(issue => issue.QuestionId.HasValue)
                .Select(issue => issue.QuestionId!.Value).Distinct().ToArray();
            throw new AnswerValidationException(
                string.Join(" ", evaluation.AnswerIssues.Select(issue => issue.Message)), invalidQuestionIds);
        }

        var score = evaluation.Score;

        var submission = new Submission
        {
            ApplicationId = request.ApplicationId,
            ApplicationVersionId = request.ApplicationVersionId,
            UserId = request.UserId,
            SubmittedUtc = DateTime.UtcNow,
            TotalScore = score.Total
        };

        var pointsByQuestion = score.Questions.ToDictionary(questionScore => questionScore.QuestionId, questionScore => questionScore.Points);
        foreach (var (questionId, answer) in request.Answers.Where(pair => evaluation.VisibleQuestionIds.Contains(pair.Key)))
        {
            var storedAnswer = new Answer
            {
                QuestionId = questionId,
                TextValue = answer.Text,
                NumberValue = answer.Number,
                PointsEarned = pointsByQuestion[questionId]
            };

            if (answer.SelectedOptionIds is not null)
            {
                foreach (var optionId in answer.SelectedOptionIds)
                {
                    storedAnswer.SelectedOptions.Add(new AnswerSelectedOption { QuestionOptionId = optionId });
                }
            }

            submission.Answers.Add(storedAnswer);
        }

        foreach (var categoryScore in score.Categories)
        {
            submission.CategoryScores.Add(new SubmissionCategoryScore
            {
                CategoryId = categoryScore.CategoryId,
                Score = categoryScore.Score
            });
        }

        await _submissionRepository.AddAsync(submission, ct);

        return submission;
    }

}

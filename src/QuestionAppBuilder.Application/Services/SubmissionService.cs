using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain;
using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Exceptions;
using QuestionAppBuilder.Domain.Interfaces;

namespace QuestionAppBuilder.Application.Services;

public sealed class SubmissionService : ISubmissionService
{
    private readonly IApplicationRepository _applicationRepository;
    private readonly ISubmissionRepository _submissionRepository;
    private readonly IScoringEngine _scoringEngine;

    public SubmissionService(
        IApplicationRepository applicationRepository,
        ISubmissionRepository submissionRepository,
        IScoringEngine scoringEngine)
    {
        ArgumentNullException.ThrowIfNull(applicationRepository);
        ArgumentNullException.ThrowIfNull(submissionRepository);
        ArgumentNullException.ThrowIfNull(scoringEngine);

        _applicationRepository = applicationRepository;
        _submissionRepository = submissionRepository;
        _scoringEngine = scoringEngine;
    }

    public async Task<Submission> SubmitAsync(SubmitRequest request, CancellationToken ct = default)
    {
        var graph = await _applicationRepository.GetVersionGraphAsync(request.ApplicationVersionId, ct);
        if (graph.Version.Status != VersionStatus.Published || graph.Version.ApplicationId != request.ApplicationId)
        {
            throw new NotFoundException(
                $"Published application version {request.ApplicationVersionId} of application {request.ApplicationId} was not found.");
        }

        var questionsById = graph.Categories
            .SelectMany(category => category.Elements)
            .Where(element => element.Question is not null)
            .Select(element => element.Question!)
            .ToDictionary(question => question.Id);

        var structurallyInvalid = new List<int>();
        foreach (var (questionId, answer) in request.Answers)
        {
            if (!questionsById.TryGetValue(questionId, out var question) || !IsStructurallyValid(question, answer))
            {
                structurallyInvalid.Add(questionId);
            }
        }

        if (structurallyInvalid.Count > 0)
        {
            throw new AnswerValidationException(
                $"Invalid answers for questions: {string.Join(", ", structurallyInvalid)}.",
                structurallyInvalid);
        }

        var missingRequired = new List<int>();
        foreach (var category in graph.Categories)
        {
            foreach (var element in category.Elements)
            {
                var question = element.Question;
                if (question is null || !question.IsRequired)
                {
                    continue;
                }

                var answer = request.Answers.TryGetValue(question.Id, out var provided) ? provided : null;
                if (!AnswerSemantics.IsAnswered(question.AnswerType, answer))
                {
                    missingRequired.Add(question.Id);
                }
            }
        }

        if (missingRequired.Count > 0)
        {
            throw new AnswerValidationException(
                $"Missing required answers for questions: {string.Join(", ", missingRequired)}.",
                missingRequired);
        }

        var score = _scoringEngine.Score(graph, request.Answers);

        var submission = new Submission
        {
            ApplicationId = request.ApplicationId,
            ApplicationVersionId = request.ApplicationVersionId,
            UserId = request.UserId,
            SubmittedUtc = DateTime.UtcNow,
            TotalScore = score.Total
        };

        var pointsByQuestion = score.Questions.ToDictionary(questionScore => questionScore.QuestionId, questionScore => questionScore.Points);
        foreach (var (questionId, answer) in request.Answers)
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

    private static bool IsStructurallyValid(Question question, AnswerValue answer) => question.AnswerType switch
    {
        AnswerType.Textbox or AnswerType.Textarea =>
            answer.Number is null
            && answer.SelectedOptionIds is null
            && (answer.Text is null || answer.Text.Length <= ValidationLimits.AnswerTextMaxLength),
        AnswerType.Number =>
            answer.Text is null
            && answer.SelectedOptionIds is null,
        AnswerType.Dropdown or AnswerType.RadioGroup =>
            answer.Text is null
            && answer.Number is null
            && (answer.SelectedOptionIds is null || IsValidSelection(question, answer.SelectedOptionIds, allowMultiple: false)),
        AnswerType.Checkboxes =>
            answer.Text is null
            && answer.Number is null
            && (answer.SelectedOptionIds is null || IsValidSelection(question, answer.SelectedOptionIds, allowMultiple: true)),
        _ => false
    };

    private static bool IsValidSelection(Question question, IReadOnlyCollection<int> selectedOptionIds, bool allowMultiple)
    {
        if (selectedOptionIds.Distinct().Count() != selectedOptionIds.Count)
        {
            return false;
        }

        if (!allowMultiple && selectedOptionIds.Count > 1)
        {
            return false;
        }

        var questionOptionIds = question.Options.Select(option => option.Id).ToHashSet();
        return selectedOptionIds.All(questionOptionIds.Contains);
    }
}

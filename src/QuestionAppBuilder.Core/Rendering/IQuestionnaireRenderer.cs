using QuestionAppBuilder.Application.Evaluation;
using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Core.Rendering;

public interface IQuestionnaireRenderer
{
    QuestionnaireRenderModel Create(ApplicationVersionGraph definition, IReadOnlyDictionary<int, AnswerValue> answers,
        EvaluationResult evaluation, bool preview);
}

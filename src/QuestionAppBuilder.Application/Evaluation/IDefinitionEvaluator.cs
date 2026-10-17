using QuestionAppBuilder.Application.Scoring;
using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Evaluation;

public interface IDefinitionEvaluator
{
    EvaluationResult Evaluate(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers, bool requireComplete);
}

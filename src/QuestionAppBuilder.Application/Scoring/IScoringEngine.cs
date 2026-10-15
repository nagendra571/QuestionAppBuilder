using QuestionAppBuilder.Domain.Graphs;

namespace QuestionAppBuilder.Application.Scoring;

public interface IScoringEngine
{
    SubmissionScore Score(ApplicationVersionGraph graph, IReadOnlyDictionary<int, AnswerValue> answers);
}

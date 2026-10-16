using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Application.Services;

public interface IApplicationDesignService
{
    Task<IReadOnlyList<ApplicationType>> ListApplicationTypesAsync(CancellationToken ct = default);
    Task<ApplicationEntity> CreateApplicationAsync(string name, int applicationTypeId, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationEntity>> ListApplicationsAsync(CancellationToken ct = default);
    Task<ApplicationEntity> GetApplicationAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> GetDraftGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph?> TryGetDraftGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> GetLatestPublishedGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> OpenDraftAsync(int applicationId, CancellationToken ct = default);
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

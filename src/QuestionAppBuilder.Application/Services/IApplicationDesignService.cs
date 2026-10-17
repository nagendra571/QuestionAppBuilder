using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;
using QuestionAppBuilder.Domain.Graphs;
using ApplicationEntity = QuestionAppBuilder.Domain.Entities.Application;

namespace QuestionAppBuilder.Application.Services;

public interface IApplicationDesignService
{
    Task<IReadOnlyList<ApplicationType>> ListApplicationTypesAsync(CancellationToken ct = default);
    Task<ApplicationType> CreateApplicationTypeAsync(string name, CancellationToken ct = default);
    Task<ApplicationEntity> CreateApplicationAsync(string name, int applicationTypeId, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationEntity>> ListApplicationsAsync(CancellationToken ct = default);
    Task<ApplicationEntity> GetApplicationAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> GetDraftGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph?> TryGetDraftGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> GetLatestPublishedGraphAsync(int applicationId, CancellationToken ct = default);
    Task<ApplicationVersionGraph> OpenDraftAsync(int applicationId, CancellationToken ct = default);
    Task<Category> AddCategoryAsync(int applicationId, string name, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task UpdateCategoryAsync(int categoryId, string name, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task RemoveCategoryAsync(int categoryId, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task<Category> DuplicateCategoryAsync(int categoryId, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task MoveCategoryAsync(int categoryId, int targetIndex, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task<Element> AddContentElementAsync(int categoryId, string label, string? helpText = null, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task<Question> AddQuestionAsync(int categoryId, string label, AnswerType answerType, bool isRequired = true, string? helpText = null, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task<QuestionOption> AddOptionAsync(int questionId, string label, string value, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task<ScoringRule> AddRuleAsync(int questionId, RuleFamily family, int points, MembershipOperator? membershipOperator = null, IReadOnlyCollection<int>? optionIds = null, decimal? minValue = null, decimal? maxValue = null, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task<Element> DuplicateElementAsync(int elementId, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task MoveElementAsync(int elementId, int targetIndex, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task<VisibilityCondition> AddVisibilityPredicateAsync(int elementId, int sourceQuestionId, ConditionOperator conditionOperator,
        ConditionJunction junction, decimal? numberValue = null, IReadOnlyCollection<int>? optionIds = null,
        CancellationToken ct = default, byte[]? expectedRevision = null);
    Task RemoveElementAsync(int elementId, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task RemoveOptionAsync(int optionId, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task RemoveRuleAsync(int ruleId, CancellationToken ct = default, byte[]? expectedRevision = null);
    Task PublishAsync(int applicationId, CancellationToken ct = default, byte[]? expectedRevision = null);
}

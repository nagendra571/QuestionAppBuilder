using QuestionAppBuilder.Domain.Entities;
using QuestionAppBuilder.Domain.Enums;

namespace QuestionAppBuilder.Editor.ViewModels;

public sealed class RuleFormViewModel
{
    public RuleFamily Family { get; set; }

    public int Points { get; set; }

    public MembershipOperator? MembershipOperator { get; set; }

    public List<int> OptionIds { get; set; } = new();

    public decimal? MinValue { get; set; }

    public decimal? MaxValue { get; set; }

    public IReadOnlyList<RuleFamily> Families { get; } = Enum.GetValues<RuleFamily>();

    public IReadOnlyList<MembershipOperator> MembershipOperators { get; } = Enum.GetValues<MembershipOperator>();

    public IReadOnlyList<QuestionOption> Options { get; set; } = Array.Empty<QuestionOption>();
}

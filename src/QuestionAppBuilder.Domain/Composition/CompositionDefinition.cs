using System.Text.Json;
using System.Text.Json.Serialization;

namespace QuestionAppBuilder.Domain.Composition;

public enum SourceKind { Constant, Question, Calculation, OptionSelected }
public enum CalculationOperation { Sum, Percentage }
public enum RulePurpose { Validation, Score }
public enum Comparison { Equal, NotEqual, GreaterThan, GreaterThanOrEqual, LessThan, LessThanOrEqual }
public sealed class ValueSource
{
    public SourceKind Kind { get; set; }
    public Guid Key { get; set; }
    public decimal Value { get; set; }
}
public sealed class QuestionGroup
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid CategoryKey { get; set; }
    public bool TableLayout { get; set; } = true;
    public List<string> Columns { get; set; } = [];
    public List<GroupRow> Rows { get; set; } = [];
}
public sealed class GroupRow
{
    public Guid QuestionKey { get; set; }
    public List<string> Cells { get; set; } = [];
}
public sealed class CalculatedField
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid CategoryKey { get; set; }
    public CalculationOperation Operation { get; set; }
    public List<ValueSource> Sources { get; set; } = [];
}
public sealed class ValueCheck
{
    public ValueSource Left { get; set; } = new();
    public Comparison Comparison { get; set; }
    public ValueSource Right { get; set; } = new();
}
public sealed class CompositionRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid CategoryKey { get; set; }
    public Guid TargetQuestionKey { get; set; }
    public RulePurpose Kind { get; set; }
    public string Message { get; set; } = "";
    public bool ShowWhileAnswering { get; set; }
    public int Points { get; set; }
    public string BandGroup { get; set; } = "";
    public bool AnyCheck { get; set; }
    public List<ValueCheck> Conditions { get; set; } = [];
    public List<ValueCheck> Checks { get; set; } = [];
}
public sealed class CompositionDefinition
{
    public List<QuestionGroup> Groups { get; set; } = [];
    public List<CalculatedField> Calculations { get; set; } = [];
    public List<CompositionRule> Rules { get; set; } = [];
    public DynamicTextDefinition DynamicText { get; set; } = new();
    private static readonly JsonSerializerOptions Options = new() { Converters = { new JsonStringEnumConverter() }, MaxDepth = 24 };
    public static CompositionDefinition Read(string? json) => string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<CompositionDefinition>(json, Options) ?? throw new JsonException("Configuration is empty.");
    public string ToJson() => JsonSerializer.Serialize(this, Options);
}

public sealed class DynamicTextDefinition
{
    public string TimeZoneId { get; set; } = "UTC";
    public List<DynamicVariable> Variables { get; set; } = [];
    public List<DynamicTemplate> Templates { get; set; } = [];
}

public enum DynamicValueKind { Text, Number, Date, TextList }
public enum DynamicValueState { Present, Missing, NotApplicable, Invalid }
public enum DynamicSourceKind { Text, Number, Date, Question, Variable, Calculation, Today }
public enum DynamicOperation { Value, JoinText, FormatDate, Age, Add, Subtract, Multiply, Divide }
public enum DynamicTextSlot { ElementLabel, ElementHelpText, ReviewSummary, ConfirmationText }
public enum DynamicPartKind { Literal, Value }

public sealed class DynamicSource
{
    public DynamicSourceKind Kind { get; set; }
    public Guid Key { get; set; }
    public string? Text { get; set; }
    public decimal? Number { get; set; }
    public DateOnly? Date { get; set; }
}

public sealed class DynamicVariable
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public Guid? CategoryKey { get; set; }
    public DynamicOperation Operation { get; set; }
    public List<DynamicSource> Sources { get; set; } = [];
    public string Separator { get; set; } = " ";
    public bool SkipEmpty { get; set; } = true;
    public string DateFormat { get; set; } = "MM-dd-yyyy";
}

public sealed class DynamicTextPart
{
    public DynamicPartKind Kind { get; set; }
    public string Text { get; set; } = "";
    public DynamicSource? Source { get; set; }
    public string Fallback { get; set; } = "";
}

public sealed class DynamicTemplate
{
    public DynamicTextSlot Slot { get; set; }
    public Guid OwnerKey { get; set; }
    public List<DynamicTextPart> Parts { get; set; } = [];
    public bool HideWhenUnavailable { get; set; }
}

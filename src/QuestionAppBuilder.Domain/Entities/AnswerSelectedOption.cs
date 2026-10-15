namespace QuestionAppBuilder.Domain.Entities;

public class AnswerSelectedOption
{
    public int Id { get; set; }
    public int AnswerId { get; set; }
    public int QuestionOptionId { get; set; }
}

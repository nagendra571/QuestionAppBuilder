namespace QuestionAppBuilder.Application.Access;

public interface IResponseAccessLinkBuilder
{
    Uri CreateLink(string token);
}

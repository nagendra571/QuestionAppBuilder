namespace QuestionAppBuilder.Application.Access;

public interface IResponseEmailSender
{
    Task SendLinkAsync(string email, Uri link, CancellationToken cancellationToken = default);
}

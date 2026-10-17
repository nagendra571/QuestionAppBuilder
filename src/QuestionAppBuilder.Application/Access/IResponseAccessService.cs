namespace QuestionAppBuilder.Application.Access;

public interface IResponseAccessService
{
    Task RequestAsync(AccessLinkRequest request, CancellationToken cancellationToken = default);
    Task<AccessGrant> RedeemAsync(string token, CancellationToken cancellationToken = default);
}

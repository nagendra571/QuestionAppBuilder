namespace QuestionAppBuilder.Application.Responses;

public interface IAvailabilityService
{
    Task SetAsync(AvailabilityUpdate update, CancellationToken cancellationToken = default);
}

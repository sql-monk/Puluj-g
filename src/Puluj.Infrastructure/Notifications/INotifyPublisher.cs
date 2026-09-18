namespace Puluj.Infrastructure.Notifications;

public interface INotifyPublisher
{
    Task PublishAsync(PulujEvent evt, CancellationToken ct = default);
}

using Microsoft.AspNetCore.SignalR;
using Puluj.Contracts;

namespace Puluj.Api.Hubs;

/// <summary>Server-to-client contract (spec §19 realtime updates). Clients call nothing; they subscribe and receive.</summary>
public interface IMapClient
{
    Task TrackUpserted(TrackDto track);
    Task TrackClosed(TrackDto track);
    Task AlertChanged(AlertDto alert);
    Task TargetCreated(TargetDto target);
    /// <summary>The listener may have missed pushes while reconnecting; reload the current window.</summary>
    Task Resync(DateTimeOffset at);
}

public sealed class MapHub : Hub<IMapClient>
{
}

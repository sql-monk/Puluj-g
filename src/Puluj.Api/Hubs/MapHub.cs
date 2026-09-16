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
    /// <summary>P11 (ADR-0011): a new incident (revision 1).</summary>
    Task IncidentUpserted(IncidentDto incident);
    /// <summary>P11: any later revision — state, suppressed, location, sources; the client drops what is no longer visible and ignores a revision it already has.</summary>
    Task IncidentRevised(IncidentDto incident);
    /// <summary>P11 (ADR-0011): this replica may have missed pushes (its LISTEN connection was re-established) — reload the window from the API; `at` is the server clock.</summary>
    Task Resync(DateTimeOffset at);
}

public sealed class MapHub : Hub<IMapClient>
{
}

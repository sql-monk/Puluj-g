using Puluj.Api.Hubs;
using Puluj.Contracts;

namespace Puluj.Api.Services;

/// <summary>
/// Map push adapter (plan §8.6, ADR-0011): which hub method an incident change becomes. A first revision is an
/// <c>IncidentUpserted</c>; everything after — a link, a confirmation, a resolution, a suppression, a merge — is an
/// <c>IncidentRevised</c> carrying the whole DTO, so the client needs no per-change method: it keeps the highest revision it
/// has seen and drops what is no longer visible (retracted, suppressed, outside its window).
/// </summary>
public static class IncidentPush
{
    public static Task SendAsync(IMapClient clients, IncidentDto incident, int? announcedRevision) =>
        (announcedRevision ?? incident.Revision) <= 1 && incident.Revision <= 1 ? clients.IncidentUpserted(incident) : clients.IncidentRevised(incident);
}

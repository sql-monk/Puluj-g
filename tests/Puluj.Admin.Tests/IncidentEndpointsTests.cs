using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Puluj.Admin.Endpoints;
using Puluj.Infrastructure.Messaging;
using Puluj.Processing.Incidents;

namespace Puluj.Admin.Tests;

/// <summary>P10 incident commands: the state writer's outcomes map to 404/409/400, and a command without its event (outbox off) is refused.</summary>
public class IncidentEndpointsTests
{
    private static IOptions<MessagingOptions> Messaging(bool outbox) => Options.Create(new MessagingOptions { Outbox = new MessagingOptions.OutboxOptions { Enabled = outbox } });

    [Fact]
    public async Task Outcomes_map_to_status_codes()
    {
        Assert.Equal(StatusCodes.Status404NotFound, await Status(() => throw new IncidentNotFoundException(7)));
        Assert.Equal(StatusCodes.Status409Conflict, await Status(() => throw new IncidentConflictException("retracted")));
        Assert.Equal(StatusCodes.Status400BadRequest, await Status(() => throw new ArgumentException("actor and reason are required")));
        Assert.Equal(StatusCodes.Status200OK, await Status(() => Task.FromResult(Results.Ok())));
    }

    [Fact]
    public async Task Command_without_the_outbox_is_refused_before_it_runs()
    {
        var ran = false;
        var result = await IncidentEndpoints.Guard(Messaging(false), null, CancellationToken.None, () =>
        {
            ran = true;
            return Task.FromResult(Results.Ok());
        });
        Assert.Equal(StatusCodes.Status409Conflict, ((IStatusCodeHttpResult)result).StatusCode);
        Assert.False(ran);
    }

    private static async Task<int?> Status(Func<Task<IResult>> action) =>
        ((IStatusCodeHttpResult)await IncidentEndpoints.Guard(Messaging(true), null, CancellationToken.None, action)).StatusCode;
}

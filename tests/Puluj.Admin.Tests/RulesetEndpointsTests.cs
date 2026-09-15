using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Puluj.Admin.Endpoints;
using Puluj.Infrastructure.Rules;

namespace Puluj.Admin.Tests;

/// <summary>P08 authoring API: mutations without actor/reason are refused, and the service's outcomes map to 404/409/422/400.</summary>
public class RulesetEndpointsTests
{
    [Theory]
    [InlineData(null, "why")]
    [InlineData("  ", "why")]
    [InlineData("me", null)]
    [InlineData("me", "")]
    public void Mutation_without_actor_or_reason_is_a_400(string? actor, string? reason)
    {
        var result = RulesetEndpoints.Missing(actor, reason);
        Assert.NotNull(result);
        Assert.Equal(StatusCodes.Status400BadRequest, ((IStatusCodeHttpResult)result).StatusCode);
    }

    [Fact]
    public void Mutation_with_actor_and_reason_passes()
    {
        Assert.Null(RulesetEndpoints.Missing("ops", "canary of v3"));
    }

    [Fact]
    public async Task Service_outcomes_map_to_status_codes()
    {
        Assert.Equal(StatusCodes.Status404NotFound, await Status(() => throw new RulesetNotFoundException(7)));
        Assert.Equal(StatusCodes.Status409Conflict, await Status(() => throw new RulesetConflictException("published")));
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, await Status(() => throw new RulesetValidationException(new ValidationReport(false, [new ValidationIssue("error", "r", "unknown_kind", "x")], []))));
        Assert.Equal(StatusCodes.Status400BadRequest, await Status(() => throw new ArgumentException("actor and reason are required")));
        Assert.Equal(StatusCodes.Status200OK, await Status(() => Task.FromResult(Results.Ok())));
    }

    private static async Task<int?> Status(Func<Task<IResult>> action) => ((IStatusCodeHttpResult)await RulesetEndpoints.Guard(action)).StatusCode;
}

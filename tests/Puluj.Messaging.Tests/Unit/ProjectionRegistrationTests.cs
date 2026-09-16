using Microsoft.Extensions.DependencyInjection;
using Puluj.Processing;
using Puluj.Processing.Projection;

namespace Puluj.Messaging.Tests.Unit;

/// <summary>P11 (review B1): the projection consumer is registered by its own extension whenever the role is present — the default
/// `messaging` service (no domain writers) must run it, a writer-less process must not silently leave the required queue without a consumer.</summary>
public class ProjectionRegistrationTests
{
    [Fact]
    public void Default_messaging_roles_register_the_projection_consumer()
    {
        var services = new ServiceCollection();
        services.AddPulujProjection(new HashSet<string> { "relay", "archive", "raw-writer", "normalizer", "parser", "llm-worker", "finalizer", StageRoles.Projection }, "test");
        Assert.Contains(services, d => d.ServiceType == typeof(ProjectionHandler));
        Assert.Contains(services, d => d.ServiceType == typeof(SubscriptionConsumer));
    }

    [Fact]
    public void Without_the_role_nothing_is_registered()
    {
        var services = new ServiceCollection();
        services.AddPulujProjection(new HashSet<string> { "relay", "archive" }, "test");
        Assert.DoesNotContain(services, d => d.ServiceType == typeof(ProjectionHandler));
    }
}

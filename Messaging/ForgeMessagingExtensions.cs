using Forge.Core.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Talaria.Core.Abstractions;
using Talaria.Transports.InMemory;

namespace Forge.Messaging;

/// <summary>
/// Composition-root registration for the messaging module. One transport
/// instance per process (<c>messaging.transport</c> config key:
/// <c>inmemory</c> default; <c>servicebus</c> reserved for the Azure
/// Service Bus transport when it lands in Talaria). The DashboardHost's
/// separate WebApplication container must be handed this SAME instance
/// (see Program.cs) so endpoint publication and orchestrator consumers
/// share the in-memory channels.
/// </summary>
public static class ForgeMessagingExtensions
{
    public const string TransportConfigKey = "messaging.transport";

    public static IServiceCollection AddForgeMessaging(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton<ITransport>(_ => CreateTransport(config));
        services.AddSingleton<IEventPublisher, TalariaEventPublisher>();
        return services;
    }

    private static ITransport CreateTransport(IConfiguration config)
    {
        var kind = config[TransportConfigKey];
        return kind switch
        {
            null or "" or "inmemory" => new InMemoryTransport(),
            "servicebus" => throw new InvalidOperationException(
                "messaging.transport=servicebus is reserved; the Azure Service Bus transport has not landed in Talaria yet."),
            _ => throw new InvalidOperationException($"Unknown messaging.transport '{kind}' (expected: inmemory | servicebus)."),
        };
    }
}

using Flowline.Core.Services.OrphanCleanup;
using Flowline.Core.Services.OrphanCleanup.Handlers;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;

namespace Flowline.Core.Tests.OrphanCleanup;

public class HandlerRegistryTests
{
    // KTD7: the handler set is resolved as IEnumerable<IOrphanHandler> via DI — the same fan-out
    // convention IPostDeployService already uses (see
    // docs/solutions/architecture-patterns/post-deploy-service-di-fanout-protocol.md). That convention's
    // documented tradeoff — a missing registration silently resolves to zero handlers rather than
    // throwing — is accepted here for consistency rather than special-cased. This test documents that
    // accepted behavior against a real ServiceCollection/ServiceProvider, not a hand-rolled stand-in.
    [Fact]
    public void ResolvingWithNoRegisteredHandlers_ReturnsEmptySequenceWithoutThrowing()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var handlers = provider.GetRequiredService<IEnumerable<IOrphanHandler>>();

        Assert.Empty(handlers);
    }

    // U9: completes the arity guard stubbed in U1. Mirrors Program.cs's registration block (a
    // ServiceCollection standing in for the real one, same DI container/convention) — a dropped
    // registration here is caught at CI time instead of silently resolving to fewer handlers in
    // production (KTD7's accepted missing-registration tradeoff is about DI resolution itself, not
    // about this test's job of catching an accidental omission). Update this expected set whenever a
    // future handler is added (KTD7 — this guard is a standing convention, not a one-time ship-time
    // check).
    [Fact]
    public void ProductionContainer_RegistersAllEightR14HandlerClasses()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IAnsiConsole>(new TestConsole());
        services.AddSingleton<IOrphanHandler, PluginAssemblyFamilyHandler>();
        services.AddSingleton<IOrphanHandler, WebResourceHandler>();
        services.AddSingleton<IOrphanHandler, WorkflowHandler>();
        services.AddSingleton<IOrphanHandler, CustomApiFamilyHandler>();
        services.AddSingleton<IOrphanHandler, BotHandler>();
        services.AddSingleton<IOrphanHandler, ConnectionReferenceHandler>();
        services.AddSingleton<IOrphanHandler, RoleHandler>();
        services.AddSingleton<IOrphanHandler, EntityFamilyHandler>();

        using var provider = services.BuildServiceProvider();
        var handlers = provider.GetRequiredService<IEnumerable<IOrphanHandler>>().ToList();

        Assert.Equal(8, handlers.Count);
        Assert.Equal(8, handlers.Select(h => h.GetType()).Distinct().Count());
        Assert.Contains(handlers, h => h is PluginAssemblyFamilyHandler);
        Assert.Contains(handlers, h => h is WebResourceHandler);
        Assert.Contains(handlers, h => h is WorkflowHandler);
        Assert.Contains(handlers, h => h is CustomApiFamilyHandler);
        Assert.Contains(handlers, h => h is BotHandler);
        Assert.Contains(handlers, h => h is ConnectionReferenceHandler);
        Assert.Contains(handlers, h => h is RoleHandler);
        Assert.Contains(handlers, h => h is EntityFamilyHandler);
    }
}

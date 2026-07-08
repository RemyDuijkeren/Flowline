using Flowline.Core.Services.OrphanCleanup;
using Microsoft.Extensions.DependencyInjection;

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

    // U1 is pure scaffolding — no concrete IOrphanHandler implementation exists yet (those land in
    // U2-U8), so there is nothing to assert an arity of eight against. U9's Files list already expects
    // to "complete the arity guard stubbed in U1" once Program.cs registers all eight R14 handler
    // classes (mirroring IPostDeployService's services.AddSingleton<IOrphanHandler, XHandler>()
    // registration convention).
    [Fact(Skip = "No concrete IOrphanHandler implementations exist yet (U2-U8) — U9 completes this arity guard once Program.cs registers all eight R14 handlers.")]
    public void ProductionContainer_RegistersAllEightR14HandlerClasses()
    {
    }
}

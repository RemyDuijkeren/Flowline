using Flowline.Core.OrphanCleanup.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Flowline.Core.OrphanCleanup;

// Single source of truth for the eight IOrphanHandler DI registrations, shared by Program.cs's
// production container and HandlerRegistryTests' arity guard test — a hand-copied list in the test
// could drift from this one (e.g. a future handler added to only one), which would go uncaught. Order
// here has no effect on execution/report sequencing (OrphanCleanupService.FamilyOrder owns that) — this
// list only decides what's registered, not what order it runs in.
public static class OrphanHandlerRegistration
{
    public static void RegisterOrphanHandlers(IServiceCollection services)
    {
        services.AddSingleton<IOrphanHandler, PluginAssemblyFamilyHandler>();
        services.AddSingleton<IOrphanHandler, WebResourceHandler>();
        services.AddSingleton<IOrphanHandler, WorkflowHandler>();
        services.AddSingleton<IOrphanHandler, CustomApiFamilyHandler>();
        services.AddSingleton<IOrphanHandler, BotHandler>();
        services.AddSingleton<IOrphanHandler, ConnectionReferenceHandler>();
        services.AddSingleton<IOrphanHandler, RoleHandler>();
        services.AddSingleton<IOrphanHandler, EntityFamilyHandler>();
    }
}

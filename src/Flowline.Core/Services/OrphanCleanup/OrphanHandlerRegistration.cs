using Flowline.Core.Services.OrphanCleanup.Handlers;
using Microsoft.Extensions.DependencyInjection;

namespace Flowline.Core.Services.OrphanCleanup;

// Single source of truth for the eight IOrphanHandler DI registrations, shared by Program.cs's
// production container and HandlerRegistryTests' arity guard test (code review: that test previously
// hand-copied this same list into its own ServiceCollection, so a drift between the two — e.g. a future
// handler added to one but not the other — went uncaught). Order here has no effect on execution/report
// sequencing (KTD1 owns that via OrphanCleanupService.FamilyOrder) — this list only decides what's
// registered, not what order it runs in.
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

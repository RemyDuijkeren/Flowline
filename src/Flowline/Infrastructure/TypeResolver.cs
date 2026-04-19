using Spectre.Console.Cli;

namespace Flowline.Infrastructure;

sealed class TypeResolver(IServiceProvider provider) : ITypeResolver, IDisposable
{
    public object? Resolve(Type? type) =>
        type == null ? null : provider.GetService(type);

    public void Dispose() => (provider as IDisposable)?.Dispose();
}

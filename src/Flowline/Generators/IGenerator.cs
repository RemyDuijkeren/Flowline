using Flowline.Config;

namespace Flowline.Generators;

public interface IGenerator
{
    GeneratorType Type { get; }
    Task RunAsync(GenerationContext context, CancellationToken cancellationToken = default);
}

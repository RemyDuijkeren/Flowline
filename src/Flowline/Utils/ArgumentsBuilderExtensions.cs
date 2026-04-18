using CliWrap.Builders;

namespace Flowline.Utils;

public static class ArgumentsBuilderExtensions
{
    public static ArgumentsBuilder AddIf(this ArgumentsBuilder builder, bool condition, string argument)
    {
        if (condition)
        {
            builder.Add(argument);
        }

        return builder;
    }

    public static ArgumentsBuilder AddIfNotNull(this ArgumentsBuilder builder, string? argument)
    {
        if (argument is not null)
        {
            builder.Add(argument);
        }

        return builder;
    }

    public static ArgumentsBuilder AddIf<T>(this ArgumentsBuilder builder, bool condition, string argument, T value)
    {
        if (condition)
        {
            builder.Add(argument).Add(value?.ToString() ?? string.Empty);
        }

        return builder;
    }

    public static ArgumentsBuilder AddIf(this ArgumentsBuilder builder, bool condition, string[]? arguments)
    {
        if (condition && arguments is not null)
        {
            foreach (var arg in arguments)
            {
                builder.Add(arg);
            }
        }

        return builder;
    }

    public static ArgumentsBuilder AddIfNotNull(this ArgumentsBuilder builder, string[]? arguments)
    {
        if (arguments is not null)
        {
            foreach (var arg in arguments)
            {
                builder.Add(arg);
            }
        }

        return builder;
    }
}

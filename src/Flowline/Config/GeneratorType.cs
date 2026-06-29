using System.Text.Json.Serialization;

namespace Flowline.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratorType
{
    Pac,
    XrmContext3,
    XrmContext,
}

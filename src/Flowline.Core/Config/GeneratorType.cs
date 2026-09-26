using System.Text.Json.Serialization;

namespace Flowline.Core.Config;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum GeneratorType
{
    Pac,
    XrmContext3,
    XrmContext,
    Ebg,
}

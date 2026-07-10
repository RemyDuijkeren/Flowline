using System.Security.Cryptography;
using System.Text;

namespace Flowline.Core.Models;

public enum FormEventType
{
    OnLoad,
    OnSave
}

public record FormEventAnnotation(string Entity, string Form, FormEventType Event, string? FunctionName, string? Parameters);

public record FormHandler(string FunctionName, string LibraryName, Guid HandlerUniqueId, string Parameters)
{
    public virtual bool Equals(FormHandler? other) =>
        other is not null
        && string.Equals(FunctionName, other.FunctionName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(LibraryName, other.LibraryName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(FunctionName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(LibraryName));
}

public record FormLibraryEntry(string Name, Guid LibraryUniqueId)
{
    public virtual bool Equals(FormLibraryEntry? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}

public record DataverseForm(Guid Id, string Name, string EntityLogicalName, string FormXml);

public static class FormEventDeterministicId
{
    public static Guid ForHandler(string entity, string form, FormEventType evt, string functionName, string libraryName) =>
        Derive(entity, form, evt.ToString(), functionName, libraryName);

    public static Guid ForLibrary(string libraryName) =>
        Derive(libraryName);

    static Guid Derive(params string[] parts)
    {
        var key = string.Concat(parts.Select(p => $"{p.Length}:{p.ToLowerInvariant()}"));
        return new Guid(SHA256.HashData(Encoding.UTF8.GetBytes(key))[..16]);
    }
}

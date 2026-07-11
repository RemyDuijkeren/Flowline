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

public record ResolvedFormEventAnnotation(FormEventAnnotation Annotation, string LibraryName, string Content, string SourceFile);

public record FormEventSnapshot(
    IReadOnlyList<ResolvedFormEventAnnotation> Annotations,
    IReadOnlySet<string> TrackedLibraryNames,
    IReadOnlyDictionary<(string Entity, string Form), DataverseForm> Forms);

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

// R18a: an unrecognized Handler (tracked library, ID doesn't match Flowline's deterministic derivation)
// paired with the annotation text the user could add instead of removing it — built from the handler's
// own stored FunctionName/LibraryName/Parameters, per FormEventPlanner.BuildProposedAnnotation.
public record UnrecognizedHandler(FormHandler Handler, string ProposedAnnotation);

public record FormEventFormPlan(
    Guid FormId,
    string EntityLogicalName,
    string FormName,
    FormEventType Event,
    IReadOnlySet<FormHandler> DesiredHandlers,
    IReadOnlySet<UnrecognizedHandler> UnrecognizedHandlers,
    IReadOnlySet<FormLibraryEntry> DesiredLibraries);

public class FormEventSyncPlan
{
    public List<FormEventFormPlan> Forms { get; } = [];

    // A form with both onLoad and onSave annotations produces two Forms entries sharing one FormId
    // (grouped by (Entity, Form, Event) upstream) — count distinct forms, not plan entries.
    public int DistinctFormCount => Forms.Select(f => f.FormId).Distinct().Count();
}

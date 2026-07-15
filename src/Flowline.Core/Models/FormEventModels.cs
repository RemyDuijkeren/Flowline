using System.Security.Cryptography;
using System.Text;

namespace Flowline.Core.Models;

public enum FormEventType
{
    OnLoad,
    OnSave,
    OnChange,
    TabStateChange,
    OnReadyStateComplete
}

// Attribute is null for OnLoad/OnSave, always non-null for OnChange/TabStateChange/OnReadyStateComplete
// (enforced by the parser) — reused as the tab-name / IFRAME-control-id scope token for the latter two.
// BulkEdit/Order are annotation modifiers ([bulkEdit], [order:N]); semantic validation (BulkEdit is
// onload-only, duplicate Order per event/scope) is a planner concern, not enforced here.
public record FormEventAnnotation(string Entity, string Form, FormEventType Event, string? FunctionName, string? Parameters, string? Attribute = null, bool BulkEdit = false, int? Order = null);

public record FormEventHandler(string FunctionName, string LibraryName, Guid HandlerUniqueId, string Parameters)
{
    public virtual bool Equals(FormEventHandler? other) =>
        other is not null
        && string.Equals(FunctionName, other.FunctionName, StringComparison.OrdinalIgnoreCase)
        && string.Equals(LibraryName, other.LibraryName, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        HashCode.Combine(
            StringComparer.OrdinalIgnoreCase.GetHashCode(FunctionName),
            StringComparer.OrdinalIgnoreCase.GetHashCode(LibraryName));
}

public record FormLibrary(string Name, Guid LibraryUniqueId)
{
    public virtual bool Equals(FormLibrary? other) =>
        other is not null && string.Equals(Name, other.Name, StringComparison.OrdinalIgnoreCase);

    public override int GetHashCode() =>
        StringComparer.OrdinalIgnoreCase.GetHashCode(Name);
}

// RowVersion (Dataverse's optimistic-concurrency token, aka @odata.etag) is always returned by the
// platform regardless of the requested ColumnSet — it's a first-class SDK/protocol property, not a
// regular attribute, so no explicit column selection is needed to populate it. Nullable because
// test-built snapshots and any row where the platform genuinely omits it must stay representable.
public record DataverseForm(Guid Id, string Name, string EntityLogicalName, string FormXml, string? RowVersion = null);

public record ResolvedFormEventAnnotation(FormEventAnnotation Annotation, string LibraryName, string Content, string SourceFile);

public record FormEventSnapshot(
    IReadOnlyList<ResolvedFormEventAnnotation> Annotations,
    IReadOnlySet<string> TrackedLibraryNames,
    IReadOnlyDictionary<(string Entity, string Form), DataverseForm> Forms);

public static class FormEventDeterministicId
{
    // attribute is non-null only for OnChange — injected between the event name and functionName so the
    // key space stays distinct per (event, attribute) while onload/onsave callers (attribute: null) are
    // byte-for-byte unaffected.
    public static Guid ForHandler(string entity, string form, FormEventType evt, string functionName, string libraryName, string? attribute = null) =>
        attribute is null
            ? Derive(entity, form, evt.ToString(), functionName, libraryName)
            : Derive(entity, form, evt.ToString(), attribute, functionName, libraryName);

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
public record UnrecognizedHandler(FormEventHandler Handler, string ProposedAnnotation);

// Attribute is null for OnLoad/OnSave, non-null for OnChange/TabStateChange/OnReadyStateComplete — carries
// the scope-token dimension from planner to executor so GetHandlers/SetHandlers can locate the right
// container-scoped <event> element (there is no other channel between the two).
// DesiredHandlers is an ordered list, not a set: the planner computes handler order once per (event, scope)
// key ([order:N] ascending first, then encounter order) and that order must survive to the FormXml write —
// callers must de-duplicate by identity without discarding position (Distinct-style, not ToHashSet).
// BulkEditEnabled is only meaningful for OnLoad plans (BehaviorInBulkEditForm is a whole-event attribute,
// not per-handler) — the OR-across-annotations union the planner computes for the form's onload event.
public record FormEventFormPlan(
    Guid FormId,
    string EntityLogicalName,
    string FormName,
    FormEventType Event,
    IReadOnlyList<FormEventHandler> DesiredHandlers,
    IReadOnlySet<UnrecognizedHandler> UnrecognizedHandlers,
    IReadOnlySet<FormLibrary> DesiredLibraries,
    string? Attribute = null,
    bool BulkEditEnabled = false);

public class FormEventSyncPlan
{
    public List<FormEventFormPlan> Forms { get; } = [];

    // A form with both onLoad and onSave annotations produces two Forms entries sharing one FormId
    // (grouped by (Entity, Form, Event) upstream) — count distinct forms, not plan entries.
    public int DistinctFormCount => Forms.Select(f => f.FormId).Distinct().Count();
}

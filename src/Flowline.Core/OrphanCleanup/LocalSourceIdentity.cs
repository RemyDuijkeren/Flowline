namespace Flowline.Core.OrphanCleanup;

// R12/KTD4: the shape is declared by the handler that matched the orphan, never re-derived from the
// component type — connection references, copilots and the custom API family carry environment-assigned
// type codes that identify nothing on their own, so a locator dispatching on the code would have
// nothing to confirm against.
//
// The three CONCEPTS.md local-source identity shapes are the categories; the cases below name the
// family within a category, because the folder and file convention differs per family even when the
// category does not. Each case is annotated with the category it belongs to.
public enum LocalIdentityShape
{
    // No mapping — resolves to nothing, and so to Undetermined (R8).
    None = 0,

    // Own file: Roles/<name>.xml
    RoleFile,

    // Own file: WebResources/<name> — the Dataverse name is itself the relative path, separators and all.
    WebResourceFile,

    // Schema-named folder: <folder>/<key>/ with no GUID anywhere locally (bots, customapis).
    SchemaNamedFolder,

    // Inline in a shared file: Other/Customizations.xml, in its own <connectionreference> declaration.
    ConnectionReferenceInline,

    // Inline in a shared file: Entities/<entity>/Entity.xml, in its own <attribute> declaration.
    // Forms and views live in sibling files (FormXml/, SavedQueries/), so scoping to Entity.xml already
    // excludes a column being dropped from a form — which is reduced usage, not removal from source.
    EntityAttributeInline,
}

// What a handler knows about where its orphan's identity lives in local source. Built through the
// factories below so Owner's per-shape meaning stays out of call sites.
public sealed record LocalSourceIdentity
{
    LocalSourceIdentity(LocalIdentityShape shape, string key, string? owner = null)
    {
        Shape = shape;
        Key   = key;
        Owner = owner;
    }

    public LocalIdentityShape Shape { get; }

    // Role name, web resource name, schema/unique name, or logical name — whichever identifies this
    // component locally.
    public string Key { get; }

    // Per shape: the folder segment for SchemaNamedFolder, the owning entity for EntityAttributeInline,
    // null otherwise.
    public string? Owner { get; }

    // The default every finding starts from — an entry nobody declared a shape for resolves to nothing.
    public static readonly LocalSourceIdentity None = new(LocalIdentityShape.None, string.Empty);

    public static LocalSourceIdentity Role(string name) =>
        new(LocalIdentityShape.RoleFile, name);

    public static LocalSourceIdentity WebResource(string name) =>
        new(LocalIdentityShape.WebResourceFile, name);

    // folderSegment is the folder relative to the solution source root, e.g. "bots", "customapis", or
    // "customapis/<parent uniquename>/customapirequestparameters" for a custom API child.
    public static LocalSourceIdentity SchemaNamedFolder(string folderSegment, string key) =>
        new(LocalIdentityShape.SchemaNamedFolder, key, folderSegment);

    public static LocalSourceIdentity ConnectionReference(string logicalName) =>
        new(LocalIdentityShape.ConnectionReferenceInline, logicalName);

    public static LocalSourceIdentity EntityAttribute(string entityName, string logicalName) =>
        new(LocalIdentityShape.EntityAttributeInline, logicalName, entityName);
}

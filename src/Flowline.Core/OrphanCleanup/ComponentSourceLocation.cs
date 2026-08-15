namespace Flowline.Core.OrphanCleanup;

// The three CONCEPTS.md local-source identity shapes, as the lookup has to interrogate them.
public enum SourceLocationKind
{
    // One file is the component. It is gone from source when that path is gone.
    File,

    // A schema-named folder is the component. It is gone from source when nothing under it remains.
    Folder,

    // The component is one declaration inside a file that outlives it. The file is still there; the
    // declaration is not. Only a removal of one of InlineMarkers counts.
    Inline,
}

// Where a component's identity lives in the unpacked solution source. RelativePath is relative to the
// solution source root and uses forward slashes — U3 rebases it onto the checkout, because on deploy
// the compare ran against a temp extraction that has no history at all (KTD2). Never build an absolute
// path here, and never touch the filesystem to produce one: on the deploy path the file this describes
// does not exist where the compare was reading.
public sealed record ComponentSourceLocation
{
    ComponentSourceLocation(SourceLocationKind kind, string relativePath, IReadOnlyList<string>? inlineMarkers = null)
    {
        Kind          = kind;
        RelativePath  = relativePath;
        InlineMarkers = inlineMarkers ?? [];
    }

    public SourceLocationKind Kind { get; }

    // Case is NOT authoritative. Several segments are composed from a live Dataverse name whose casing
    // does not match what pac wrote to disk — an entity folder is schema-cased (Entities/Account) while
    // the handler only has the logical name (account), which is why ComponentClassifier matches those
    // folders with OrdinalIgnoreCase rather than composing a path at all. Git pathspecs are
    // case-sensitive, so a consumer must match this path case-insensitively (`:(icase)`) or it will
    // silently find nothing and report a real removal as never-in-source.
    public string RelativePath { get; }

    // Inline only: the literal source tokens whose deletion means this component's own declaration went
    // away. More than one because a shape can be written more than one way — pac emits a connection
    // reference's logical name as an XML attribute in some solutions and as a child element in others,
    // and either encoding disappearing is the same removal.
    //
    // Deliberately the literal token and not a bare identifier: solution-source format is the engine's
    // knowledge, and keeping it here leaves the CLI adapter with nothing to know but git.
    public IReadOnlyList<string> InlineMarkers { get; }

    public static ComponentSourceLocation File(string relativePath) =>
        new(SourceLocationKind.File, relativePath);

    public static ComponentSourceLocation Folder(string relativePath) =>
        new(SourceLocationKind.Folder, relativePath);

    public static ComponentSourceLocation Inline(string relativePath, params string[] markers) =>
        new(SourceLocationKind.Inline, relativePath, markers);
}

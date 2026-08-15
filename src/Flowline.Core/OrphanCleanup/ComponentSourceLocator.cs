namespace Flowline.Core.OrphanCleanup;

// R4/R12/KTD4: consumes the shape a handler already declared on its finding — never re-derives it from
// the Dataverse component type, which several families (connection references, copilots, the custom API
// family) carry only as an environment-assigned code that identifies nothing on its own.
//
// Pure string composition — no File.Exists/Directory.* here. On the deploy path the compare that
// produced the orphan ran against a temp extraction, so an existence check against the checkout would
// test the wrong tree; U3 is what rebases these relative paths onto a real one.
public static class ComponentSourceLocator
{
    public static ComponentSourceLocation? Locate(LocalSourceIdentity identity)
    {
        if (string.IsNullOrWhiteSpace(identity.Key)) return null;

        return identity.Shape switch
        {
            LocalIdentityShape.RoleFile =>
                ComponentSourceLocation.File($"Roles/{identity.Key}.xml"),

            // The Dataverse name is itself the relative path (may contain '/') — see
            // LocalSourceIdentity.WebResource.
            LocalIdentityShape.WebResourceFile =>
                ComponentSourceLocation.File($"WebResources/{identity.Key}"),

            LocalIdentityShape.SchemaNamedFolder when !string.IsNullOrWhiteSpace(identity.Owner) =>
                ComponentSourceLocation.Folder($"{identity.Owner}/{identity.Key}"),

            // pac emits a connection reference's logical name as an XML attribute in some solutions and
            // as a child element in others — either encoding disappearing is the same removal, so both
            // literal tokens are markers (see ComponentSourceLocation.InlineMarkers).
            LocalIdentityShape.ConnectionReferenceInline =>
                ComponentSourceLocation.Inline(
                    "Other/Customizations.xml",
                    $"connectionreferencelogicalname=\"{identity.Key}\"",
                    $"<connectionreferencelogicalname>{identity.Key}</connectionreferencelogicalname>"),

            // Mirrors ComponentClassifier.ScanEntityAttributeLogicalNames: the owning entity's folder
            // under Entities/, holding one Entity.xml with the attribute's LogicalName inline.
            LocalIdentityShape.EntityAttributeInline when !string.IsNullOrWhiteSpace(identity.Owner) =>
                ComponentSourceLocation.Inline(
                    $"Entities/{identity.Owner}/Entity.xml",
                    $"<LogicalName>{identity.Key}</LogicalName>"),

            // None, or a shape whose required Owner is missing — no mapping, resolves to Undetermined (R8).
            _ => null,
        };
    }
}

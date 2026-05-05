using System;

namespace Flowline.Attributes
{

/// <summary>
/// The field type of a Custom API input parameter or output value, used for Flowline registration.
/// </summary>
/// <remarks>
/// Used in <see cref="InputAttribute"/> and <see cref="OutputAttribute"/> to declare the
/// Dataverse type of each parameter. Choose the value that matches the C# type you read from
/// or write to <c>context.InputParameters</c> / <c>context.OutputParameters</c>:
/// <list type="table">
///   <listheader><term>C# type</term><description><c>FieldType</c></description></listheader>
///   <item><term><c>bool</c></term><description><see cref="Boolean"/></description></item>
///   <item><term><c>DateTime</c></term><description><see cref="DateTime"/></description></item>
///   <item><term><c>decimal</c></term><description><see cref="Decimal"/></description></item>
///   <item><term><c>Entity</c></term><description><see cref="Entity"/></description></item>
///   <item><term><c>EntityCollection</c></term><description><see cref="EntityCollection"/></description></item>
///   <item><term><c>EntityReference</c></term><description><see cref="EntityReference"/></description></item>
///   <item><term><c>float</c> / <c>double</c></term><description><see cref="Float"/></description></item>
///   <item><term><c>int</c></term><description><see cref="Integer"/></description></item>
///   <item><term><c>Money</c></term><description><see cref="Money"/></description></item>
///   <item><term><c>OptionSetValue</c></term><description><see cref="Picklist"/></description></item>
///   <item><term><c>string</c></term><description><see cref="String"/></description></item>
///   <item><term><c>string[]</c></term><description><see cref="StringArray"/></description></item>
///   <item><term><c>Guid</c></term><description><see cref="Guid"/></description></item>
/// </list>
/// </remarks>
public enum FieldType
{
    /// <summary>A true/false value. Maps to C# <c>bool</c>.</summary>
    Boolean          = 0,

    /// <summary>A date and time value. Maps to C# <c>DateTime</c>.</summary>
    DateTime         = 1,

    /// <summary>A fixed-precision decimal number. Maps to C# <c>decimal</c>.</summary>
    Decimal          = 2,

    /// <summary>
    /// A full Dataverse record snapshot. Maps to C# <c>Entity</c>.
    /// Requires <see cref="InputAttribute.Entity"/> or <see cref="OutputAttribute.Entity"/>
    /// to be set to the table logical name.
    /// </summary>
    Entity           = 3,

    /// <summary>A collection of Dataverse records. Maps to C# <c>EntityCollection</c>.</summary>
    EntityCollection = 4,

    /// <summary>
    /// A reference to a Dataverse record (table + ID). Maps to C# <c>EntityReference</c>.
    /// Requires <see cref="InputAttribute.Entity"/> or <see cref="OutputAttribute.Entity"/>
    /// to be set to the table logical name.
    /// </summary>
    EntityReference  = 5,

    /// <summary>A floating-point number. Maps to C# <c>float</c> or <c>double</c>.</summary>
    Float            = 6,

    /// <summary>A whole number. Maps to C# <c>int</c>.</summary>
    Integer          = 7,

    /// <summary>A currency value. Maps to C# <c>Money</c>.</summary>
    Money            = 8,

    /// <summary>An option set (choice) value. Maps to C# <c>OptionSetValue</c>.</summary>
    Picklist         = 9,

    /// <summary>A text value. Maps to C# <c>string</c>.</summary>
    String           = 10,

    /// <summary>An array of text values. Maps to C# <c>string[]</c>.</summary>
    StringArray      = 11,

    /// <summary>A unique identifier. Maps to C# <c>Guid</c>.</summary>
    Guid             = 12,
}
}

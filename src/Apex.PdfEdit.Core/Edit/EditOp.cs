using System.Text.Json.Serialization;

namespace Apex.PdfEdit.Core.Edit;

/// <summary>
/// One edit operation from an <c>edits.json</c>. Sealed hierarchy —
/// System.Text.Json resolves the concrete record from the <c>type</c> discriminator.
///
/// See <c>prep/edits_json_draft.md</c> for the format.
/// </summary>
/// <param name="Id">Caller-assigned identifier for this op — used in error messages.</param>
/// <param name="Type">Discriminator matching the JSON <c>type</c> field.</param>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SetTextOp), typeDiscriminator: "setText")]
[JsonDerivedType(typeof(AddParagraphOp), typeDiscriminator: "addParagraph")]
[JsonDerivedType(typeof(AddListItemOp), typeDiscriminator: "addListItem")]
[JsonDerivedType(typeof(DeleteNodeOp), typeDiscriminator: "deleteNode")]
public abstract record EditOp(string Id, [property: JsonIgnore] string Type);

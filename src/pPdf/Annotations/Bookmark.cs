namespace pPdf.Annotations;

/// <summary>A page the user marked to come back to, with a name of their choice. Kept with the annotations; "Save a copy" adds it to the outline.</summary>
public sealed record Bookmark(int Page, string Title);

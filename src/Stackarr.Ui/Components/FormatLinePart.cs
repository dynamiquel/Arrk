namespace Stackarr.Ui.Components;

/// <summary>
/// One coloured token on a compact format subline, e.g. "1080p" or "Dolby Atmos".
/// <see cref="Variant"/> selects the colour family (<c>resolution</c>, <c>hdr</c>, <c>audio</c>) and
/// <see cref="Modifier"/> refines it into a specific token class (e.g. <c>hdr</c> + <c>dolby-vision</c>
/// → <c>st-format-line--hdr-dolby-vision</c>).
/// </summary>
public sealed record FormatLinePart(string Text, string Variant, string? Modifier = null);

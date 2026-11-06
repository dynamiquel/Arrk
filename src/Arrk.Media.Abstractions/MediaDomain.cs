namespace Arrk.Media.Abstractions;

/// <summary>
/// The broad media domain an item belongs to. This is deliberately an open string value rather
/// than a central enum: each feature module (and any plugin) defines its own domain keys, so a new
/// media type never requires a core enum change or a switch update here.
/// </summary>
public readonly record struct MediaDomain(string Value) : IComparable<MediaDomain>
{
    public bool IsEmpty => string.IsNullOrWhiteSpace(Value);

    public override string ToString() => Value;

    public int CompareTo(MediaDomain other) => StringComparer.Ordinal.Compare(Value, other.Value);

    public static implicit operator MediaDomain(string value) => new(value);

    public static implicit operator string(MediaDomain domain) => domain.Value;
}

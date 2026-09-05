namespace DurableLinq;

/// <summary>A value that carries no information. Used as the result of steps that return nothing.</summary>
public readonly record struct Unit
{
    public static readonly Unit Value = default;
    public override string ToString() => "()";
}

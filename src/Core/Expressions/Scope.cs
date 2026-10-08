namespace CircuitRF.Core.Expressions;

/// <summary>
/// A single frame in the structural scope chain (§9).
/// Bindings map a name → (expression text, units suffix).
/// User-defined functions are stored separately and visible globally.
/// </summary>
public sealed class Scope
{
    private readonly Dictionary<string, (string Expression, string? Unit)> _bindings = new(StringComparer.Ordinal);
    private readonly Scope? _parent;
    private readonly string _debugName;

    public Scope(string debugName, Scope? parent = null)
    {
        _debugName = debugName;
        _parent = parent;
    }

    public string DebugName => _debugName;

    /// <summary>
    /// The full path of the cell INSTANCE this scope belongs to (<c>X1.X2</c>), or null for the testbench's global
    /// scope. Inherited: a frame with no path of its own (a user function's call frame) belongs to the scope it was
    /// called from.
    ///
    /// <para>This is what makes a distribution call a <b>process</b> or a <b>mismatch</b> draw (yield overview D6):
    /// one evaluated in a scope with no instance path is shared by every instance in a trial, one evaluated in a
    /// cell instance's scope is drawn again for each instance. A property of the scope, never of the function.</para>
    /// </summary>
    public string? InstancePath
    {
        get => _instancePath ?? _parent?.InstancePath;
        init => _instancePath = value;
    }
    private readonly string? _instancePath;

    public void Bind(string name, string expression, string? unit = null)
        => _bindings[name] = (expression, unit);

    /// <summary>
    /// Looks up a binding, walking the chain outward.
    /// Returns (expression, unit, owningScope) or null if not found.
    /// </summary>
    public (string Expression, string? Unit, Scope Owner)? Lookup(string name)
    {
        if (_bindings.TryGetValue(name, out var b))
            return (b.Expression, b.Unit, this);
        return _parent?.Lookup(name);
    }

    public override string ToString() => _debugName;
}

/// <summary>
/// A user-defined function: ordered parameter names + body expression text.
/// </summary>
public sealed class UserFunction(string name, string[] parameters, string body)
{
    public string   Name       { get; } = name;
    public string[] Parameters { get; } = parameters;
    public string   Body       { get; } = body;
    public Expr     BodyAst    { get; } = Parser.Parse(body);
}

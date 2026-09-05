namespace DurableLinq;

/// <summary>
/// Parses and formats instance keys of the form <c>{namespace}/{workflowName}/v{version}/{instanceId}</c>.
/// </summary>
public readonly record struct InstanceKey(string Namespace, string WorkflowName, int Version, string InstanceId)
{
    public static string Format(string ns, string workflowName, int version, string instanceId)
    {
        Validate(ns, nameof(ns));
        Validate(workflowName, nameof(workflowName));
        if (string.IsNullOrWhiteSpace(instanceId)) throw new ArgumentException("Instance id is required.", nameof(instanceId));
        if (instanceId.Contains('/')) throw new ArgumentException("Instance id must not contain '/'.", nameof(instanceId));
        if (version < 0) throw new ArgumentOutOfRangeException(nameof(version));
        return $"{ns}/{workflowName}/v{version}/{instanceId}";
    }

    public static InstanceKey Parse(string key)
    {
        if (!TryParse(key, out var parsed))
            throw new FormatException($"'{key}' is not a valid instance key. Expected '{{namespace}}/{{workflow}}/v{{version}}/{{instanceId}}'.");
        return parsed;
    }

    public static bool TryParse(string key, out InstanceKey result)
    {
        result = default;
        if (string.IsNullOrEmpty(key)) return false;
        var parts = key.Split('/', 4);
        if (parts.Length != 4) return false;
        if (parts[2].Length < 2 || parts[2][0] != 'v' || !int.TryParse(parts[2].AsSpan(1), out var version)) return false;
        if (parts[0].Length == 0 || parts[1].Length == 0 || parts[3].Length == 0) return false;
        result = new InstanceKey(parts[0], parts[1], version, parts[3]);
        return true;
    }

    public override string ToString() => Format(Namespace, WorkflowName, Version, InstanceId);

    private static void Validate(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException($"{name} is required.", name);
        if (value.Contains('/')) throw new ArgumentException($"{name} must not contain '/'.", name);
    }
}

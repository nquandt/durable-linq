namespace RedisWorkers.Workflows;

/// <summary>Reads <c>WORKFLOW_STYLE</c>. <c>async</c> selects the workflow written as an async method; anything else selects the query version.</summary>
public static class WorkflowStyle
{
    public static bool IsAsync =>
        string.Equals(Environment.GetEnvironmentVariable("WORKFLOW_STYLE"), "async", StringComparison.OrdinalIgnoreCase);
}

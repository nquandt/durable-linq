namespace WebApiHosting.Models;

public record OrderStatusResponse(string Key, string Status, string[] Steps, object? Result, DurableLinq.WorkflowError? Error);

namespace PostgresApprovals.Models;

public record ExpenseStatusResponse(string Key, string Status, Decision? Decision);

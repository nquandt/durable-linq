namespace PostgresApprovals.Models;

public record Decision(bool Approved, string DecidedBy, string Reason);

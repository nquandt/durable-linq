namespace PostgresApprovals.Models;

public record Review(string By, string? Comment, bool Approved = false);

namespace ConsoleFileStore.Models;

public sealed record Receipt(string AuthCode, string Tracking, decimal Total);

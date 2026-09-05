namespace ConsoleFileStore.Models;

public sealed record Order(string Id, string CustomerId, Line[] Lines);

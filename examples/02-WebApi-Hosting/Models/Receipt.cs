namespace WebApiHosting.Models;

public record Receipt(string AuthCode, DateTimeOffset ChargedAt, bool Delivered);

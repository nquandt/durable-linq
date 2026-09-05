namespace ConsoleFileStore.Services;

public interface IShipping
{
    Task<string> CreateLabelAsync(string address);
}

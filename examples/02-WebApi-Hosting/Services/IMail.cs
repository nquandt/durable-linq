namespace WebApiHosting.Services;

public interface IMail
{
    Task Send(string to, string body);
}

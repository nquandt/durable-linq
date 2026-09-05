namespace WebApiHosting.Services;

public sealed class FakeMail(ILogger<FakeMail> log) : IMail
{
    public Task Send(string to, string body)
    {
        log.LogInformation("mail to {To}: {Body}", to, body);
        return Task.CompletedTask;
    }
}

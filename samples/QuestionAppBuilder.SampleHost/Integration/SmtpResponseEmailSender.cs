using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using QuestionAppBuilder.Application.Access;

namespace QuestionAppBuilder.SampleHost.Integration;

public sealed class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string FromAddress { get; set; } = string.Empty;
    public string? FromName { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool ImplicitTls { get; set; }
}

public sealed class SmtpResponseEmailSender : IResponseEmailSender
{
    private readonly SmtpOptions _options;

    public SmtpResponseEmailSender(IOptions<SmtpOptions> options) => _options = options.Value;

    public async Task SendLinkAsync(string email, Uri link, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentNullException.ThrowIfNull(link);
        if (link.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("Response access links must use HTTPS.");

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName ?? string.Empty, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(email));
        message.Subject = "Your questionnaire access link";
        message.Body = new TextPart("plain")
        {
            Text = $"Use this secure link to open your questionnaire response:\n\n{link}\n\nIf you did not request this link, you can ignore this message."
        };

        using var client = new SmtpClient();
        var security = _options.ImplicitTls ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
        await client.ConnectAsync(_options.Host, _options.Port, security, cancellationToken);
        if (!string.IsNullOrWhiteSpace(_options.Username))
            await client.AuthenticateAsync(_options.Username, _options.Password!, cancellationToken);
        await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
    }
}

public sealed record DevelopmentMail(string Address, Uri Link);

public sealed class DevMailSink
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<DevelopmentMail> _messages = new();
    public IReadOnlyList<DevelopmentMail> Messages => _messages.ToArray();
    public void Add(DevelopmentMail message) => _messages.Enqueue(message);
}

public sealed class DevelopmentResponseEmailSender : IResponseEmailSender
{
    private readonly DevMailSink _sink;
    public DevelopmentResponseEmailSender(DevMailSink sink) => _sink = sink;
    public Task SendLinkAsync(string email, Uri link, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _sink.Add(new DevelopmentMail(email, link));
        return Task.CompletedTask;
    }
}

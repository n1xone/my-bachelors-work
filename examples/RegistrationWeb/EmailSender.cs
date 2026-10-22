using System.Net;
using System.Net.Mail;

public sealed class EmailSender(IConfiguration config, IWebHostEnvironment environment)
{
    public bool IsPickup => config["Smtp:Delivery"] == "Pickup" && environment.IsDevelopment();

    public async Task SendAsync(string email, string subject, string body, CancellationToken ct)
    {
        using var message = new MailMessage(config["Smtp:From"] ?? "registration@localhost", email, subject, body);
        using var smtp = new SmtpClient();
        if (IsPickup)
        {
            var directory = Path.Combine(environment.ContentRootPath, "Data", "mail");
            Directory.CreateDirectory(directory);
            smtp.DeliveryMethod = SmtpDeliveryMethod.SpecifiedPickupDirectory;
            smtp.PickupDirectoryLocation = directory;
        }
        else
        {
            smtp.Host = config["Smtp:Host"] ?? throw new InvalidOperationException("Configure Smtp:Host.");
            smtp.Port = config.GetValue("Smtp:Port", 587);
            smtp.EnableSsl = true;
            smtp.Credentials = new NetworkCredential(config["Smtp:Username"], config["Smtp:Password"]);
        }
        await smtp.SendMailAsync(message, ct);
    }
}

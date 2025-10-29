namespace BaseUsuarios.Api.Config;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int    Port { get; set; } = 587;
    public string User { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromName { get; set; } = "Proyecto";
    public bool   UseStartTls { get; set; } = true;

    public string FromEmail => User;
}

using Adminbot.Domain;

public class ServerInfo
{
    public string Username { get; set; }
    public string Password { get; set; }
    public string Url { get; set; }
    public string RootPath { get; set; }
    public VMessConfiguration VmessTemplate { get; set; }
    public Vless Vless { get; set; }
    public List<Inbound> Inbounds { get; set; }
}



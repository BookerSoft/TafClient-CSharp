namespace TafClient.Config;

/// <summary>
/// Port of <c>com.faforever.client.config.ClientProperties</c> (@Data @ConfigurationProperties).
/// Bound from appsettings.json "faf-client" section.
/// </summary>
public class ClientProperties
{
    public string MainWindowTitle { get; set; } = "Downlord's TAF Client";
    public ServerProperties Server { get; set; } = new();
    public IrcProperties Irc { get; set; } = new();
    public ReplayProperties Replay { get; set; } = new();
    public ApiProperties Api { get; set; } = new();
    public WebsiteProperties Website { get; set; } = new();
    public ForgedAllianceProperties ForgedAlliance { get; set; } = new();
    public GalacticWarProperties GalacticWar { get; set; } = new();
    public TadaProperties Tada { get; set; } = new();
}

/// <summary>Port of ClientProperties.Server.</summary>
public class ServerProperties
{
    public string Host { get; set; } = "lobby.taforever.com";
    public int Port    { get; set; } = 8001;
}

/// <summary>Port of ClientProperties.Irc.</summary>
public class IrcProperties
{
    public string Host        { get; set; } = "irc.taforever.com";
    public int Port           { get; set; } = 8167;
    public int ReconnectDelay { get; set; } = 5_000;
}

/// <summary>Port of ClientProperties.Replay.</summary>
public class ReplayProperties
{
    public string RemoteHost   { get; set; } = "lobby.taforever.com";
    public int RemotePort      { get; set; } = 15000;
    public string FileFormat   { get; set; } = "%d-%s.tad";
    public string FileGlob     { get; set; } = "*.tad";
    public int WatchDelaySeconds { get; set; } = 300;

    /// <summary>Port: demo compiler port = remote port.</summary>
    public int CompilerPort    => RemotePort;
    /// <summary>Port: replay server port = remote port + 1.</summary>
    public int ReplayServerPort => RemotePort + 1;
}

/// <summary>Port of ClientProperties.Api.</summary>
public class ApiProperties
{
    public string BaseUrl    { get; set; } = "https://api.taforever.com";
    public string ClientId   { get; set; } = "taf-client";
    public string ClientSecret { get; set; } = string.Empty;
    public int MaxPageSize   { get; set; } = 10_000;
}

/// <summary>Port of ClientProperties.Website.</summary>
public class WebsiteProperties
{
    public string BaseUrl           { get; set; } = "https://www.taforever.com";
    public string ForgotPasswordUrl { get; set; } = "https://www.taforever.com/account/password/reset";
    public string CreateAccountUrl  { get; set; } = "https://www.taforever.com/account/register";
    public string ReportUrl         { get; set; } = "https://www.taforever.com/account/report";
}

/// <summary>Port of ClientProperties.ForgedAlliance.</summary>
public class ForgedAllianceProperties
{
    /// <summary>Window title to find the TA process handle. Mirrors Java windowTitle.</summary>
    public string WindowTitle { get; set; } = "Total Annihilation";
    public string? ExeUrl     { get; set; }
}

/// <summary>Port of ClientProperties.GalacticWar.</summary>
public class GalacticWarProperties
{
    public string? Url { get; set; }
}

/// <summary>Port of ClientProperties.Tada.</summary>
public class TadaProperties
{
    public string? RootUrl { get; set; }
    public string? DownloadReplayUrlRegex { get; set; }
    public string? ReplayDownloadEndpointFormat { get; set; }
}

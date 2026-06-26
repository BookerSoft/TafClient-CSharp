using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using TafClient.Net;
using TafClient.Net.Domain;

namespace TafClient.Service;

/// <summary>
/// Port of com.faforever.client.user.UserService.
///
/// LoginAsync now returns a Task that FAULTS if ConnectAndLogIn faults —
/// so callers can correctly distinguish success from failure.
/// </summary>
public class UserService
{
    private readonly ILogger<UserService> _log;
    private readonly IFafServerAccessor   _faf;
    private readonly PlayerService        _playerService;

    private readonly BehaviorSubject<string?> _username = new(null);

    private string? _password;
    private int?    _userId;

    public string? Username    => _username.Value;
    public int?    UserId      => _userId;
    public string? Password    => _password;
    public string? AccessToken { get; private set; }   // JWT from welcome message — used as --hashtoken

    public IObservable<string?> UsernameChanged => _username;

    public UserService(ILogger<UserService> log, IFafServerAccessor faf, PlayerService ps)
    {
        _log           = log;
        _faf           = faf;
        _playerService = ps;

        faf.Router.AddListener<LoginMessage>("welcome", msg => _userId = msg.Id);
    }

    /// <summary>
    /// Returns a Task that:
    ///   • Completes successfully when login succeeds (username is set).
    ///   • FAULTS with LoginFailedException when the server rejects credentials
    ///     or the connection is lost — so the LoginScreen can show the error.
    /// </summary>
    public async Task LoginAsync(string username, string password,
                                 bool autoLogin, CancellationToken ct = default)
    {
        _password = password;

        // Let ConnectAndLogIn throw — do not wrap in a ContinueWith that swallows faults.
        LoginMessage info = await _faf.ConnectAndLogIn(username, password, ct);

        // ── Success path ──────────────────────────────────────────────────────
        _userId      = info.Id;
        _username.OnNext(info.Login);
        AccessToken  = info.Token;   // JWT for --hashtoken
        _playerService.SetCurrentPlayer(info.Id, info.Login);
        _log.LogInformation("Logged in as {Login} (id={Id})", info.Login, info.Id);
    }

    public void CancelLogin()
    {
        _faf.Disconnect();
    }

    /// <summary>
    /// Port of UserService.logOut().
    /// Java: fafService.disconnect(); eventBus.post(new LoggedOutEvent());
    ///       preferencesService.getPreferences().getLogin().setAutoLogin(false);
    /// </summary>
    public void LogOut()
    {
        _log.LogInformation("Logging out user {Username}", Username);
        _faf.Disconnect();
        _username.OnNext(null);
        _userId      = null;
        _password    = null;
        AccessToken  = null;
        // TODO: persist autoLogin = false via PreferencesService
    }
}

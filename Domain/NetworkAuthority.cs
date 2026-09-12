namespace BDVM.Domain;

public enum NetworkRole
{
    Local,
    SoloHost,
    MultiplayerHost,
    MultiplayerClient,
    Indeterminate,
    ExternalAuthorityClient
}

public sealed class NetworkApiState
{
    public bool ApiAvailable { get; set; }
    public bool IsConnected { get; set; }
    public bool IsHost { get; set; }
    public bool IsSinglePlayer { get; set; }
    public bool ExternalAuthorityConfigured { get; set; }
}

public sealed class NetworkRoleReport
{
    public NetworkRole Role { get; set; }
    public bool HasAuthority { get; set; }
    public string Detail { get; set; } = "";
}

public interface INetworkRoleDetector
{
    NetworkRoleReport Detect();
}

public interface INetworkApiStateReader
{
    NetworkApiState Read();
}

public static class NetworkAuthorityPolicy
{
    public static NetworkRoleReport Classify(NetworkApiState state)
    {
        if (state.ExternalAuthorityConfigured)
            return new NetworkRoleReport { Role = NetworkRole.ExternalAuthorityClient, HasAuthority = false,
                Detail = "The dedicated backend owns economic authority; the game cannot fall back to local writes." };
        if (!state.ApiAvailable)
            return Authoritative(NetworkRole.Local, "Multiplayer API is unavailable; local authority.");

        if (!state.IsConnected)
        {
            if (state.IsHost || state.IsSinglePlayer)
                return Indeterminate("Multiplayer API reports no connection with active host/single-player flags.");
            return Authoritative(NetworkRole.Local, "Multiplayer API is loaded with no active network session; local authority.");
        }

        if (state.IsHost && state.IsSinglePlayer)
            return Authoritative(NetworkRole.SoloHost, "Connected loopback single-player server; host authority.");
        if (state.IsHost)
            return Authoritative(NetworkRole.MultiplayerHost, "Connected multiplayer server; host authority.");
        if (!state.IsSinglePlayer)
            return new NetworkRoleReport
            {
                Role = NetworkRole.MultiplayerClient,
                HasAuthority = false,
                Detail = "Connected multiplayer client; visibility is non-authoritative."
            };
        return Indeterminate("Multiplayer API reports a single-player connection without host authority.");
    }

    public static bool CanExecuteEconomy(NetworkRoleReport role, out string reason)
    {
        if (role.Role == NetworkRole.ExternalAuthorityClient)
        {
            reason = "The dedicated backend owns economic authority; send an authenticated intention to that backend.";
            return false;
        }
        if (role.HasAuthority)
        {
            reason = role.Detail;
            return true;
        }
        reason = role.Role == NetworkRole.MultiplayerClient
            ? "BDVM economy is host-only; the connected client may submit intentions but cannot execute mutations."
            : "BDVM economy is refused because network authority is indeterminate.";
        return false;
    }

    private static NetworkRoleReport Authoritative(NetworkRole role, string detail) =>
        new NetworkRoleReport { Role = role, HasAuthority = true, Detail = detail };

    private static NetworkRoleReport Indeterminate(string detail) =>
        new NetworkRoleReport { Role = NetworkRole.Indeterminate, HasAuthority = false, Detail = detail };
}

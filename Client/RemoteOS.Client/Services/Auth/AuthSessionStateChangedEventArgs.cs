namespace Client.Services.Auth;

public sealed class AuthSessionStateChangedEventArgs : EventArgs
{
    public AuthSessionState State { get; }

    public AuthSessionStateChangedEventArgs(AuthSessionState state) => State = state;
}

namespace Client.Services.Auth;

public sealed class AuthSessionStateChangedEventArgs : EventArgs
{
    public AuthSessionState State { get; }
    public RememberedProfileSaveResult? RememberedProfileSaveResult { get; }

    public AuthSessionStateChangedEventArgs(AuthSessionState state, RememberedProfileSaveResult? rememberedProfileSaveResult = null)
    {
        State = state;
        RememberedProfileSaveResult = rememberedProfileSaveResult;
    }
}

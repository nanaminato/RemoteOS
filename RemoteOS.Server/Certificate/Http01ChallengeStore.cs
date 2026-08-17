using System.Text;
using System.Text.RegularExpressions;

namespace Server.Certificate;

public interface IHttp01ChallengeStore
{
    Task PutAsync(string token, string keyAuthorization, CancellationToken cancellationToken);
    Task RemoveAsync(string token, CancellationToken cancellationToken);
}

/// <summary>WebRoot HTTP-01 token store. Tokens are constrained to ACME's URL-safe form and cannot escape the owned root.</summary>
public sealed partial class FileHttp01ChallengeStore : IHttp01ChallengeStore
{
    private readonly string _root;

    public FileHttp01ChallengeStore(CertificateOptions options)
    {
        _root = Path.GetFullPath(options.ChallengeRoot ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteOS", "acme-challenge")
            : "/var/lib/remoteos/acme-challenge"));
    }

    public async Task PutAsync(string token, string keyAuthorization, CancellationToken cancellationToken)
    {
        if (!TokenPattern().IsMatch(token) || string.IsNullOrWhiteSpace(keyAuthorization) || keyAuthorization.Length > 4096 || keyAuthorization.Any(char.IsControl))
            throw new ArgumentException("Invalid ACME HTTP-01 challenge.");
        if (Directory.Exists(_root) && File.GetAttributes(_root).HasFlag(FileAttributes.ReparsePoint))
            throw new CertificateOperationException("certificate.challenge_unsafe_path");
        Directory.CreateDirectory(_root);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = TokenPath(token);
        var temporary = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, keyAuthorization, new UTF8Encoding(false), cancellationToken);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, path, true);
            // A WebRoot deployment is useful only if the exact token that was staged can be
            // read back from the owned path. Do not let a filesystem race become a false claim
            // that HTTP-01 is ready for the CA.
            if (!string.Equals(await File.ReadAllTextAsync(path, cancellationToken), keyAuthorization, StringComparison.Ordinal))
                throw new CertificateOperationException("certificate.challenge_write_failed");
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public Task RemoveAsync(string token, CancellationToken cancellationToken)
    {
        if (TokenPattern().IsMatch(token))
        {
            var path = TokenPath(token);
            if (File.Exists(path)) File.Delete(path);
        }
        return Task.CompletedTask;
    }

    private string TokenPath(string token)
    {
        var path = Path.GetFullPath(Path.Combine(_root, token));
        if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new InvalidOperationException("Challenge path escaped its root.");
        if (File.Exists(path) && File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new CertificateOperationException("certificate.challenge_unsafe_path");
        return path;
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,256}$", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}

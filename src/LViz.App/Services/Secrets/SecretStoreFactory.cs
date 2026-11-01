using LViz.Core.Persistence;

namespace LViz.App.Services.Secrets;

/// <summary>
/// Picks the <see cref="ISecretStore"/> for the current OS: the platform
/// credential store where one exists, an in-memory store elsewhere (login
/// then lasts only for the running session). The <c>OperatingSystem.IsX()</c>
/// guards keep the platform analyzer quiet about the OS-attributed stores.
/// </summary>
internal static class SecretStoreFactory
{
    public static ISecretStore Create()
    {
        if (OperatingSystem.IsMacOS()) return new MacKeychainSecretStore();
        if (OperatingSystem.IsWindows()) return new WindowsCredentialSecretStore();
        if (OperatingSystem.IsLinux()) return new LinuxSecretServiceStore();
        return new InMemorySecretStore();
    }
}

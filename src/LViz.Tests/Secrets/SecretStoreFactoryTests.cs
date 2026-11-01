using LViz.App.Services.Secrets;
using LViz.Core.Persistence;
using Xunit;

namespace LViz.Tests.Secrets;

/// <summary>
/// <see cref="SecretStoreFactory.Create"/> must hand out the platform store
/// for the OS it runs on. Only the current OS's branch is asserted, and no
/// method is called on the returned store — a real platform store would
/// reach for the Keychain / Credential Manager / Secret Service.
/// </summary>
public class SecretStoreFactoryTests
{
    [Fact]
    public void Create_OnCurrentOS_ReturnsPlatformStore()
    {
        ISecretStore store = SecretStoreFactory.Create();

        if (OperatingSystem.IsMacOS())
            Assert.IsType<MacKeychainSecretStore>(store);
        else if (OperatingSystem.IsWindows())
            Assert.IsType<WindowsCredentialSecretStore>(store);
        else if (OperatingSystem.IsLinux())
            Assert.IsType<LinuxSecretServiceStore>(store);
        else
            Assert.IsType<InMemorySecretStore>(store);
    }
}

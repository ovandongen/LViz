using LViz.Core.Persistence;
using Xunit;

namespace LViz.Tests.Persistence;

/// <summary>
/// <see cref="InMemorySecretStore"/>: plain dictionary semantics — miss is
/// null, write replaces, delete removes and missing deletes are no-ops.
/// </summary>
public class InMemorySecretStoreTests
{
    [Fact]
    public async Task Read_MissingKey_ReturnsNull()
    {
        var store = new InMemorySecretStore();

        Assert.Null(await store.ReadAsync("github"));
    }

    [Fact]
    public async Task Write_Succeeds_AndReadReturnsValue()
    {
        var store = new InMemorySecretStore();

        Assert.True(await store.WriteAsync("github", "secret"));
        Assert.Equal("secret", await store.ReadAsync("github"));
    }

    [Fact]
    public async Task Write_Twice_ReplacesOldValue()
    {
        var store = new InMemorySecretStore();
        await store.WriteAsync("github", "first");

        Assert.True(await store.WriteAsync("github", "second"));
        Assert.Equal("second", await store.ReadAsync("github"));
    }

    [Fact]
    public async Task Delete_RemovesValue()
    {
        var store = new InMemorySecretStore();
        await store.WriteAsync("github", "secret");

        await store.DeleteAsync("github");

        Assert.Null(await store.ReadAsync("github"));
    }

    [Fact]
    public async Task Delete_MissingKey_DoesNotThrow()
    {
        var store = new InMemorySecretStore();

        await store.DeleteAsync("github");
    }
}

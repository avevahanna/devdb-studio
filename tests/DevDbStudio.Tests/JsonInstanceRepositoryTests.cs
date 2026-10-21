using DevDbStudio.Services.Dbms;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DevDbStudio.Tests;

public sealed class JsonInstanceRepositoryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "dbserve-tests-" + Guid.NewGuid().ToString("N"));
    private string FilePath => Path.Combine(_dir, "instances.json");

    private JsonInstanceRepository NewRepo() =>
        new(Options.Create(new StorageOptions { InstancesFile = FilePath }), NullLogger<JsonInstanceRepository>.Instance);

    private static DbInstance Instance(string name = "pg-1") => new()
    {
        Id = Guid.NewGuid(), Name = name, DbmsKey = "postgres", ContainerName = "dbapp-" + name, HostPort = 5432,
        CreatedUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), Status = InstanceStatus.Running,
        DatabaseName = "appdb", Username = "postgres", Password = "Secret1!x", VolumeName = "vol", ImageTag = "16",
    };

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public async Task MissingFile_GivesEmptyList() => Assert.Empty(await NewRepo().GetAllAsync());

    [Fact]
    public async Task AddedInstance_IsPersistedAndReloadedByNewRepository()
    {
        var inst = Instance();
        await NewRepo().AddAsync(inst);

        var loaded = Assert.Single(await NewRepo().GetAllAsync());

        Assert.Equal(inst, loaded);
    }

    [Fact]
    public async Task GetAsync_FindsById_AndReturnsNullForUnknown()
    {
        var repo = NewRepo();
        var inst = Instance();
        await repo.AddAsync(inst);

        Assert.Equal(inst.Name, (await repo.GetAsync(inst.Id))!.Name);
        Assert.Null(await repo.GetAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Update_ReplacesExistingInstance()
    {
        var repo = NewRepo();
        var inst = Instance();
        await repo.AddAsync(inst);

        await repo.UpdateAsync(inst with { HostPort = 6000 });

        Assert.Equal(6000, (await NewRepo().GetAsync(inst.Id))!.HostPort);
    }

    [Fact]
    public async Task Update_OfUnknownInstance_IsIgnored()
    {
        var repo = NewRepo();
        await repo.AddAsync(Instance());

        await repo.UpdateAsync(Instance("other"));

        Assert.Single(await repo.GetAllAsync());
    }

    [Fact]
    public async Task Remove_DeletesAndReportsResult()
    {
        var repo = NewRepo();
        var inst = Instance();
        await repo.AddAsync(inst);

        Assert.True(await repo.RemoveAsync(inst.Id));
        Assert.False(await repo.RemoveAsync(inst.Id));
        Assert.Empty(await NewRepo().GetAllAsync());
    }

    [Fact]
    public async Task CorruptFile_IsMovedAside_AndRepositoryStartsEmpty()
    {
        Directory.CreateDirectory(_dir);
        await File.WriteAllTextAsync(FilePath, "{ this is not valid json");

        var list = await NewRepo().GetAllAsync();

        Assert.Empty(list);
        Assert.False(File.Exists(FilePath));
        Assert.Single(Directory.GetFiles(_dir, "instances.json.corrupt-*"));
    }

    [Fact]
    public async Task ConcurrentAdds_AreAllKept()
    {
        var repo = NewRepo();

        await Task.WhenAll(Enumerable.Range(0, 20).Select(i => repo.AddAsync(Instance("n" + i))));

        Assert.Equal(20, (await NewRepo().GetAllAsync()).Count);
    }
}

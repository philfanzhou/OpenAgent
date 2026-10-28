using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Moq;
using OpenAgent.Engine.Abstractions;
using OpenAgent.Engine.Config;
using OpenAgent.Engine.Extensions;
using StackExchange.Redis;
using Xunit;

namespace OpenAgent.Engine.Tests.Config;

public class SharedRedisXmlRepositoryTests
{
    private static readonly XElement Alpha = new("key", new XAttribute("id", "alpha"));
    private static readonly XElement Beta = new("key", new XAttribute("id", "beta"));

    private static Mock<IRedisConnectionProvider> ProviderWithDatabase(Mock<IDatabase> database)
    {
        var provider = new Mock<IRedisConnectionProvider>();
        provider.Setup(p => p.GetDatabase(It.IsAny<int>())).Returns(database.Object);
        return provider;
    }

    private static Mock<IDatabase> DatabaseReturning(params HashEntry[] entries)
    {
        var database = new Mock<IDatabase>();
        database.Setup(db => db.HashGetAll(
                It.Is<RedisKey>(key => key == SharedRedisXmlRepository.KeyRingKey), CommandFlags.None))
            .Returns(entries);
        return database;
    }

    // null would fall back to the profile default directory, which other test
    // hosts in the same CI run may have populated.
    private static string AbsentLegacyDirectory() =>
        Path.Combine(Path.GetTempPath(), "dataprotection-absent-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void GetAllElements_ReturnsRedisEntries()
    {
        var provider = ProviderWithDatabase(DatabaseReturning(
            new HashEntry("key-alpha.xml", "﻿" + Alpha), // UTF-8 BOM as produced by seeding from files
            new HashEntry("key-beta.xml", Beta.ToString())));
        var repository = new SharedRedisXmlRepository(provider.Object, AbsentLegacyDirectory());

        IReadOnlyCollection<XElement> elements = repository.GetAllElements();

        Assert.Equal(2, elements.Count);
        Assert.Contains(elements, element => (string?)element.Attribute("id") == "alpha");
        Assert.Contains(elements, element => (string?)element.Attribute("id") == "beta");
    }

    [Fact]
    public async Task GetAllElements_ReadsLegacyDirectoryOnceAndSkipsDuplicates()
    {
        string root = Path.Combine(Path.GetTempPath(), "dataprotection-tests-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, ".aspnet", "DataProtection-Keys");
        Directory.CreateDirectory(legacy);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(legacy, "key-alpha.xml"), Alpha.ToString());
            await File.WriteAllTextAsync(Path.Combine(legacy, "key-gamma.xml"),
                new XElement("key", new XAttribute("id", "gamma")).ToString());
            await File.WriteAllTextAsync(Path.Combine(legacy, "broken.xml"), "<not-xml");
            var provider = ProviderWithDatabase(DatabaseReturning(new HashEntry("key-alpha.xml", Beta.ToString())));
            var repository = new SharedRedisXmlRepository(provider.Object, legacy);

            IReadOnlyCollection<XElement> elements = repository.GetAllElements();

            Assert.Equal(2, elements.Count);
            Assert.Contains(elements, element => (string?)element.Attribute("id") == "beta");
            Assert.Contains(elements, element => (string?)element.Attribute("id") == "gamma");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task GetAllElements_UsesLegacyDirectoryWhenRedisFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "dataprotection-tests-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "keys");
        Directory.CreateDirectory(legacy);
        try
        {
            await File.WriteAllTextAsync(Path.Combine(legacy, "key-alpha.xml"), Alpha.ToString());
            var database = new Mock<IDatabase>();
            database.Setup(db => db.HashGetAll(
                    It.Is<RedisKey>(key => key == SharedRedisXmlRepository.KeyRingKey), CommandFlags.None))
                .Throws(new RedisException("connection unavailable"));
            var repository = new SharedRedisXmlRepository(ProviderWithDatabase(database).Object, legacy);

            XElement element = Assert.Single(repository.GetAllElements());

            Assert.Equal("alpha", (string?)element.Attribute("id"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void StoreElement_WritesHashFieldWithXml()
    {
        var database = new Mock<IDatabase>();
        var repository = new SharedRedisXmlRepository(
            ProviderWithDatabase(database).Object, AbsentLegacyDirectory());

        repository.StoreElement(Alpha, "key-alpha.xml");

        database.Verify(db => db.HashSet(
            It.Is<RedisKey>(key => key == SharedRedisXmlRepository.KeyRingKey),
            It.Is<RedisValue>(name => name == "key-alpha.xml"),
            It.Is<RedisValue>(value => value == Alpha.ToString()),
            When.Always,
            CommandFlags.None), Times.Once);
    }

    [Fact]
    public void StoreElement_SwallowsRedisOutage()
    {
        var database = new Mock<IDatabase>();
        database.Setup(db => db.HashSet(
                It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), When.Always, CommandFlags.None))
            .Throws(new RedisException("connection unavailable"));
        var repository = new SharedRedisXmlRepository(
            ProviderWithDatabase(database).Object, AbsentLegacyDirectory());

        repository.StoreElement(Alpha, "key-alpha.xml");

        database.Verify(db => db.HashSet(
            It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), It.IsAny<RedisValue>(), When.Always, CommandFlags.None),
            Times.Once);
    }

    [Fact]
    public void AddAgentEngine_UsesSharedRedisRepositoryOnlyWhenMultiplexerIsRegistered()
    {
        IConfiguration configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();
        services.AddSingleton(configuration);
        services.AddAgentEngine(configuration);

        using ServiceProvider island = services.BuildServiceProvider();
        Assert.IsNotType<SharedRedisXmlRepository>(
            island.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);

        var multiplexer = new Mock<IConnectionMultiplexer>();
        multiplexer.SetupGet(connection => connection.IsConnected).Returns(true);
        services.AddSingleton(multiplexer.Object);
        using ServiceProvider shared = services.BuildServiceProvider();
        Assert.IsType<SharedRedisXmlRepository>(
            shared.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
    }
}

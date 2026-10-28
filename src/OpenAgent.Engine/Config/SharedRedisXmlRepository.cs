using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using OpenAgent.Engine.Abstractions;
using StackExchange.Redis;

namespace OpenAgent.Engine.Config;

/// <summary>
/// Key ring shared through Redis so that every engine instance reading the same
/// database can decrypt the same tenant secret ciphertext. The per-container
/// default key directory stays readable for ciphertext written before this
/// repository existed; new keys are written to Redis only.
/// </summary>
internal sealed class SharedRedisXmlRepository(
    IRedisConnectionProvider redis, string? legacyDirectory = null) : IXmlRepository
{
    public const string KeyRingKey = "openagent:dataprotection-keys";

    private readonly string _legacyDirectory = legacyDirectory
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".aspnet", "DataProtection-Keys");

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        List<XElement> elements = [];
        HashSet<string> names = new(StringComparer.Ordinal);
        void Add(string name, string xml)
        {
            if (names.Add(name) && Parse(xml) is { } element)
            {
                elements.Add(element);
            }
        }

        HashEntry[] entries;
        try
        {
            // Not gated on IsConnected: the ring is built once at startup while the
            // multiplexer may still be connecting; commands queue until it is up.
            entries = redis.GetDatabase().HashGetAll(KeyRingKey);
        }
        catch (RedisException)
        {
            entries = [];
        }
        foreach (HashEntry entry in entries)
        {
            Add(entry.Name.ToString(), entry.Value.ToString());
        }
        if (Directory.Exists(_legacyDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(_legacyDirectory, "*.xml"))
            {
                Add(Path.GetFileName(file), File.ReadAllText(file));
            }
        }
        return elements.AsReadOnly();
    }

    public void StoreElement(XElement element, string? friendlyName)
    {
        try
        {
            redis.GetDatabase().HashSet(KeyRingKey,
                friendlyName ?? Guid.NewGuid().ToString("N"), element.ToString());
        }
        catch (RedisException)
        {
            // The in-process ring stays usable without the persisted copy.
        }
    }

    private static XElement? Parse(string xml)
    {
        try
        {
            // Seeded key files may carry a UTF-8 BOM, which is invalid inside a string.
            return XElement.Parse(xml.TrimStart('\ufeff'));
        }
        catch (XmlException)
        {
            return null;
        }
    }
}

namespace OpenAgent.Engine.Abstractions;

public interface IConfigSnapshot
{
    T? GetConfig<T>(string key);
    void SetConfig<T>(string key, T value);
    bool TryGetConfig<T>(string key, out T? config);
    T? GetConfig<T>(string agentId, string configType);
    void SetConfig<T>(string agentId, string configType, T value);
    bool TryGetConfig<T>(string agentId, string configType, out T? config);
    void Evict(string agentId);
    void Clear();
}

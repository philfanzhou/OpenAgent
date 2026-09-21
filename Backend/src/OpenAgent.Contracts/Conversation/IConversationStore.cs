namespace OpenAgent.Contracts.Conversation;

/// <summary>读写组合契约；只读/只写消费方应依赖窄接口，DI 转发保证同一实例。</summary>
public interface IConversationStore : IConversationReader, IConversationWriter
{
}

namespace OpenAgent.Contracts.Files;

public enum FileAssetSource
{
    UserUpload,
    Agent,
    Skill
}

public enum FileAssetState
{
    Pending,
    Ready,
    Failed
}

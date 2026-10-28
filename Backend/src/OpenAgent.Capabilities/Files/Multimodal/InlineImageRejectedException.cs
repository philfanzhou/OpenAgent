namespace OpenAgent.Capabilities.Files.Multimodal;

/// <summary>
/// 内联图片在处理阶段被明确拒绝：真实格式与声明不符、数据畸形，或超出
/// 像素/内存预算。调用方捕获后应跳过该图的内联并记录原因——不得
/// 吞掉异常后把原始字节继续发给模型（小体积头部可声明超大尺寸，解码即
/// 解压炸弹）。
/// </summary>
internal sealed class InlineImageRejectedException : Exception
{
    public InlineImageRejectedException(string message)
        : base(message)
    {
    }
}

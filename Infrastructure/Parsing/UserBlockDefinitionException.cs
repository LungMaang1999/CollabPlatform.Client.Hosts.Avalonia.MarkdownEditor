namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Infrastructure.Parsing;

/// <summary>用户块定义在加载期校验失败。</summary>
public sealed class UserBlockDefinitionException : Exception
{
    public UserBlockDefinitionException(string message)
        : base(message)
    {
    }
}

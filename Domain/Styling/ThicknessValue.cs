namespace CollabPlatform.Client.Hosts.Avalonia.MarkdownEditor.Domain.Styling;

/// <summary>
/// Represents four-sided numeric padding or margin dimensions.
/// </summary>
public sealed record ThicknessValue(double Left, double Top, double Right, double Bottom)
{
    public ThicknessValue(double uniform) : this(uniform, uniform, uniform, uniform) { }
    public ThicknessValue(double horizontal, double vertical) : this(horizontal, vertical, horizontal, vertical) { }
}
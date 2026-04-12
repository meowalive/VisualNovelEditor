namespace VNEditor.Models;

public sealed class PortraitVisualState
{
    public string RoleId { get; private set; } = string.Empty;
    public double DefaultY { get; private set; }
    public double X { get; set; }
    public double Y { get; set; }
    public double Scale { get; set; } = 1.0;

    public void Reset(string roleId, double defaultY, double defaultScale)
    {
        RoleId = roleId ?? string.Empty;
        DefaultY = defaultY;
        X = 0;
        Y = defaultY;
        Scale = defaultScale;
    }

    public void Clear()
    {
        Reset(string.Empty, 0, 1.0);
    }
}

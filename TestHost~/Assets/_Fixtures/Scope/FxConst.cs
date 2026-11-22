public static class FxConstSource
{
    public const int Value = 3;
}

public enum FxMode { Slow, Fast }

class FxHidden
{
    public static int Get() => 1;
}

public class FxInternalMember
{
    internal static int Secret() => 2;
}

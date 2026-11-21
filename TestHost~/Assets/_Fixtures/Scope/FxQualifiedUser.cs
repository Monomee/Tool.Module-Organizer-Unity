using FxNs.Inner;

// Inlined constants written fully qualified and through a generic type: no IL reference to either declarer.
public class FxQualifiedUser
{
    public int a = FxNs.Inner.FxNsConst.Value;
    public int b = FxGenConst<int>.Value;
}

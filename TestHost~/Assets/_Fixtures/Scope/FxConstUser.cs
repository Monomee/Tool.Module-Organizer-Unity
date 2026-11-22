using System.Runtime.CompilerServices;

public class FxConstUser
{
    public int copy = FxConstSource.Value; // inlined: no IL reference to FxConstSource
    public void Set(int mode = (int)FxMode.Fast) { }
    public int Peek() => FxHidden.Get() + FxInternalMember.Secret();

    [MethodImpl(MethodImplOptions.AggressiveInlining)] // pseudo-attribute: a flag, not a custom attribute
    public int Fast() => copy;
}

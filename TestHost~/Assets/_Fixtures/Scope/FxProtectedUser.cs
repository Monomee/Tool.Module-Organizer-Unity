// Not derived: protected internal reaches it only inside Assembly-CSharp.
public class FxProtectedUser
{
    public int Use() => FxProtected.Shared() + FxProtected.Nested.N();
}

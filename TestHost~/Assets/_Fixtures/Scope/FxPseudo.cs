using System;

// [Serializable] is a metadata flag, typeof(SerializableAttribute) a real IL reference; the structs use the default layout.
[Serializable]
public class FxPseudo
{
    public Type kind = typeof(SerializableAttribute);
}

public struct FxPlainStruct
{
    public int x;
}

public struct FxEmptyStruct
{
}

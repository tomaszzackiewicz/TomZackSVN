public enum NodeType { Trunk, Branch, Tag, Unknown }

public readonly struct BranchInfo
{
    public readonly string Name;
    public readonly NodeType Type;

    public BranchInfo(string name, NodeType type)
    {
        Name = name;
        Type = type;
    }

    public static readonly BranchInfo Trunk = new("trunk", NodeType.Trunk);
    public static readonly BranchInfo Unknown = new("unknown", NodeType.Unknown);
}
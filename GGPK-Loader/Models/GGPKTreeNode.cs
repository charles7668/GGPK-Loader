using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace GGPK_Loader.Models;

public class GGPKTreeNode
{
    public object Value { get; set; }
    public ulong Offset { get; set; }
    public List<GGPKTreeNode> Children { get; set; } = new();

    public GGPKTreeNode(object value, ulong offset)
    {
        Value = value;
        Offset = offset;
    }
}
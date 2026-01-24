using System.Collections.Generic;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GGPK_Loader.Models;

public partial class GGPKTreeNode : ObservableObject
{
    public object Value { get; set; }
    public ulong Offset { get; set; }
    public GGPKTreeNode? Parent { get; set; }
    public List<GGPKTreeNode> Children { get; set; } = new();

    [ObservableProperty]
    private bool _isExpanded;

    [ObservableProperty]
    private bool _isSelected;

    public GGPKTreeNode(object value, ulong offset)
    {
        Value = value;
        Offset = offset;
    }

    public string Name => Value switch
    {
        GGPKFileInfo f => f.FileName,
        GGPKDirInfo d => d.Name,
        string s => s,
        _ => Value?.ToString() ?? ""
    };

    public string FullPath
    {
        get
        {
            var stack = new Stack<string>();
            var current = this;
            while (current != null)
            {
                if (!string.IsNullOrEmpty(current.Name))
                {
                    stack.Push(current.Name);
                }
                current = current.Parent;
            }

            var result = string.Join("/", stack);
            result = "/" + result.TrimStart('/');
            return result;
        }
    }
}
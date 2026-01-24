using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace GGPK_Loader.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        var dataListBox = this.FindControl<ListBox>("DataListBox");
        var indexListBox = this.FindControl<ListBox>("IndexListBox");
        var headerScrollViewer = this.FindControl<ScrollViewer>("HeaderScrollViewer");

        if (dataListBox != null)
        {
            dataListBox.AddHandler(ScrollViewer.ScrollChangedEvent, (s, e) =>
            {
                if (e.Source is not ScrollViewer mainScrollViewer)
                {
                    return;
                }

                if (indexListBox != null)
                {
                    var indexScrollViewer = indexListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                    if (indexScrollViewer != null)
                    {
                        indexScrollViewer.Offset = new Vector(indexScrollViewer.Offset.X, mainScrollViewer.Offset.Y);
                    }
                }

                if (headerScrollViewer != null)
                {
                    headerScrollViewer.Offset = new Vector(mainScrollViewer.Offset.X, headerScrollViewer.Offset.Y);
                }
            });
        }
    }
}
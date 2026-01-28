using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using GGPK_Loader.ViewModels;

// Needed for GetVisualDescendants

namespace GGPK_Loader.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        _listBox = this.FindControl<ListBox>("DataListBox");
        _indexListBox = this.FindControl<ListBox>("IndexListBox");
        _headerPanel = this.FindControl<StackPanel>("HeaderContainer");
        _rowHeaderLabel = this.FindControl<TextBlock>("RowHeaderLabel");

        DataContextChanged += OnDataContextChanged;
    }

    private readonly StackPanel? _headerPanel;
    private readonly ListBox? _indexListBox;
    private readonly ListBox? _listBox;
    private readonly TextBlock? _rowHeaderLabel;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is MainWindowViewModel vm)
        {
            vm.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(MainWindowViewModel.DatInfoSource))
                {
                    UpdateLayout(vm.DatInfoSource);
                }
            };

            if (vm.DatInfoSource != null)
            {
                UpdateLayout(vm.DatInfoSource);
            }
        }
    }

    private void UpdateLayout(MainWindowViewModel.DatRowInfo? info)
    {
        if (_listBox == null || _headerPanel == null || _indexListBox == null || _rowHeaderLabel == null)
        {
            return;
        }

        _headerPanel.Children.Clear();
        _listBox.ItemTemplate = null;
        _indexListBox.ItemTemplate = null;

        if (info == null)
        {
            return;
        }

        _rowHeaderLabel.Text = "Row";
        _rowHeaderLabel.Width = info.ColumnWidths[0];

        for (var i = 0; i < info.Headers.Count; i++)
        {
            _headerPanel.Children.Add(new TextBlock
            {
                Text = info.Headers[i],
                Width = info.ColumnWidths[i + 1], // +1 because index 0 is Row width
                FontWeight = FontWeight.Bold,
                Foreground = Brushes.LightGray,
                Margin = new Thickness(5, 0)
            });
        }

        _indexListBox.ItemTemplate = new FuncDataTemplate<MainWindowViewModel.DatRow>((_, _) =>
        {
            var tb = new TextBlock
            {
                Width = info.ColumnWidths[0],
                FontFamily = "Consolas",
                Margin = new Thickness(5, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.Gray,
                TextAlignment = TextAlignment.Right
            };
            tb.Bind(TextBlock.TextProperty, new Binding("Index"));
            return tb;
        });

        _listBox.ItemTemplate = new FuncDataTemplate<MainWindowViewModel.DatRow>((_, _) =>
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Transparent };

            var contextMenu = new ContextMenu();
            var copyItem = new MenuItem { Header = "Copy" };

            // Binding to window's datacontext
            if (DataContext is MainWindowViewModel vm)
            {
                copyItem.Bind(MenuItem.CommandProperty, new Binding
                {
                    Path = "CopyDatRowCommand",
                    Source = vm
                });
            }

            // Binding the CommandParameter to the ListBox's SelectedItems
            copyItem.Bind(MenuItem.CommandParameterProperty, new Binding
            {
                Path = "SelectedItems",
                Source = _listBox
            });

            var copyJsonItem = new MenuItem { Header = "Copy JSON" };
            if (DataContext is MainWindowViewModel viewModel)
            {
                copyJsonItem.Bind(MenuItem.CommandProperty, new Binding
                {
                    Path = "CopyDatRowJsonCommand",
                    Source = viewModel
                });
            }

            copyJsonItem.Bind(MenuItem.CommandParameterProperty, new Binding
            {
                Path = "SelectedItems",
                Source = _listBox
            });

            contextMenu.Items.Add(copyItem);
            contextMenu.Items.Add(copyJsonItem);
            panel.ContextMenu = contextMenu;

            for (var i = 0; i < info.Headers.Count; i++)
            {
                var tb = new TextBlock
                {
                    Width = info.ColumnWidths[i + 1], // +1 skip Row width
                    FontFamily = "Consolas",
                    Margin = new Thickness(5, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                tb.Bind(TextBlock.TextProperty, new Binding($"Values[{i}]"));
                panel.Children.Add(tb);
            }

            return panel;
        });

        // Sync Scroll Logic
        var headerScroll = this.FindControl<ScrollViewer>("HeaderScrollViewer");

        // When DataListBox scrolls...
        _listBox.AddHandler(ScrollViewer.ScrollChangedEvent, (_, e) =>
        {
            if (e.Source is ScrollViewer mainScrollViewer)
            {
                // Sync Horizontal Scroll -> Header
                if (headerScroll != null)
                {
                    headerScroll.Offset = new Vector(mainScrollViewer.Offset.X, headerScroll.Offset.Y);
                }

                // Sync Vertical Scroll -> IndexListBox
                // We need to find the ScrollViewer inside IndexListBox
                // We need to find the ScrollViewer inside IndexListBox
                var indexScrollViewer = _indexListBox.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
                if (indexScrollViewer != null)
                {
                    indexScrollViewer.Offset = new Vector(indexScrollViewer.Offset.X, mainScrollViewer.Offset.Y);
                }
            }
        });

        // Sync Selection
        _indexListBox.Selection = _listBox.Selection;
    }

    private void OnNodePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var properties = e.GetCurrentPoint(sender as Visual).Properties;
        if (properties.IsRightButtonPressed)
        {
            // Consume the event to prevent the TreeViewItem from processing it (which causes selection change)
            // The ContextMenu triggers on a separate event (ContextRequested) or is handled before this suppression affects it.
            e.Handled = true;
        }
    }
}
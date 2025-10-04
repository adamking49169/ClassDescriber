using System;
using System.Windows;
using System.Windows.Controls;

namespace ClassDescriber.Vsix;

public partial class ExplainToolWindowControl : UserControl
{
    public ExplainToolWindowControl()
    {
        InitializeComponent();
    }

    public void AppendResult(string file, string markdown)
    {
        var card = new Border
        {
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = System.IO.Path.GetFileName(file),
            FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 4)
        });

        stack.Children.Add(new TextBlock
        {
            Text = markdown,
            TextWrapping = TextWrapping.Wrap
        });

        card.Child = stack;
        ResultsPanel.Children.Insert(0, card);
    }
}

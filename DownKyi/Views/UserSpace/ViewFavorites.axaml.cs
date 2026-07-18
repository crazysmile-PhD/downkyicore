using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace DownKyi.Views.UserSpace;

internal partial class ViewFavorites : UserControl
{
    public ViewFavorites()
    {
        InitializeComponent();
    }

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }
}

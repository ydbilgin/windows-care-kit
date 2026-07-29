using Microsoft.Win32;
using System.Windows.Controls;
using WindowsCareKit.Module.Restore.ViewModels;

namespace WindowsCareKit.Module.Restore.Views;

public partial class RestoreView : UserControl
{
    public RestoreView()
    {
        InitializeComponent();
    }

    private void ChoosePackageFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not RestoreViewModel viewModel)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = viewModel.I18n["migration.restore.chooseFolder"],
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
            viewModel.PackageDir = dialog.FolderName;
    }
}

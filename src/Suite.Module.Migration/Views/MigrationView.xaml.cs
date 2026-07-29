using Microsoft.Win32;
using System.Windows.Controls;
using WindowsCareKit.Module.Migration.ViewModels;

namespace WindowsCareKit.Module.Migration.Views;

public partial class MigrationView : UserControl
{
    public MigrationView()
    {
        InitializeComponent();
    }

    private void ChoosePackageFolder_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if (DataContext is not MigrationViewModel viewModel)
            return;

        var dialog = new OpenFolderDialog
        {
            Title = viewModel.I18n["migration.capture.chooseFolder"],
            Multiselect = false,
        };
        if (dialog.ShowDialog() == true)
            viewModel.PackageDir = dialog.FolderName;
    }
}

using System.Windows.Controls;
using Constructora.Application.Bim;
using Constructora.Wpf.ViewModels;

namespace Constructora.Wpf;

public partial class BimView : UserControl
{
    public BimView() => InitializeComponent();

    private void OnElementosSeleccionados(object sender, SelectionChangedEventArgs e)
    {
        if (DataContext is BimViewModel viewModel && sender is DataGrid grid)
            viewModel.ActualizarSeleccion(grid.SelectedItems.Cast<ElementoModeloBimDto>().ToArray());
    }
}

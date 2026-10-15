using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Constructora.Wpf.ViewModels;
using Microsoft.Extensions.Logging;

namespace Constructora.Wpf;

public partial class MainWindow : Window
{
    private readonly CertificacionViewModel _viewModel;
    private readonly ILogger<MainWindow> _logger;
    private bool _cerrando;
    private bool _puedeCerrar;
    private int _erroresEdicion;

    public MainWindow(CertificacionViewModel viewModel, ILogger<MainWindow> logger)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _logger = logger;
        DataContext = viewModel;
    }

    private void OnEditorFocusChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        _viewModel.EditorTieneFoco = (bool)e.NewValue;

    private void OnValidationError(object sender, ValidationErrorEventArgs e)
    {
        _erroresEdicion = Math.Max(0, _erroresEdicion + (e.Action == ValidationErrorEventAction.Added ? 1 : -1));
        _viewModel.TieneErroresEdicion = _erroresEdicion > 0;
        if (_viewModel.TieneErroresEdicion) _viewModel.CertificacionActual?.MarcarModificada();
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_puedeCerrar) return;
        e.Cancel = true;
        if (_cerrando) return;
        _cerrando = true;
        IsEnabled = false;
        try
        {
            // Closing is also a local save, so an accidental close does not discard an offline draft.
            if (_viewModel.CertificacionActual?.TieneCambios == true)
            {
                await _viewModel.GuardarCertificacionCommand.ExecuteAsync(null);
                if (_viewModel.CertificacionActual?.TieneCambios == true)
                {
                    MessageBox.Show("No se pudo guardar el borrador. Revise los valores antes de cerrar.",
                        "Borrador pendiente", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
            }
            await _viewModel.DetenerAsync();
            _puedeCerrar = true;
            Close();
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "No se pudo finalizar el cliente de forma ordenada.");
            MessageBox.Show($"No se pudo completar el cierre: {exception.Message}", "Constructora",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _cerrando = false;
            IsEnabled = true;
        }
    }
}

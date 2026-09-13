using CaeManager.Application.Vehiculos.Queries.ObtenerVehiculoPorId;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Vehiculos.Components;

public partial class VehiculoPreviewDrawer : ComponentBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;

    [Parameter] public Guid? VehiculoId { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<(Guid Id, string Pestana)> OnOperar { get; set; }

    private string _pestanaActiva = "informacion";
    private Guid? _idCargado;

    private VehiculoDetalleDto? _detalle;
    private bool _cargando;

    /// <summary>
    /// Se incrementa cada vez que se abre un Vehículo distinto. La respuesta
    /// tardía de A (fila A todavía en vuelo cuando se abre la fila B sin
    /// cerrar el drawer) comprueba esto antes de escribir <see cref="_detalle"/>:
    /// sin ello, A podía pintarse bajo la vista previa de B. Mismo patrón que
    /// <c>EmpresaWorkspacePanel.razor</c>.
    /// </summary>
    private int _generacion;

    private readonly CancellationTokenSource _ciclo = new();
    private CancellationToken _cancelacion;

    protected override void OnInitialized() => _cancelacion = _ciclo.Token;

    // Reabrir sobre un Vehículo distinto (fila B tras fila A sin cerrar el
    // drawer) debe recargar todo desde cero — por eso se compara VehiculoId
    // aquí en vez de solo mirar Visible. Documentación/Historial no
    // necesitan estado propio: PestanaDocumentacion/PestanaHistorial ya
    // recargan solas cuando cambia su parámetro de propietario.
    protected override Task OnParametersSetAsync()
    {
        if (Visible && VehiculoId is { } id && _idCargado != id)
        {
            _generacion++;
            _idCargado = id;
            _pestanaActiva = "informacion";
            _detalle = null;
            return CargarInformacionAsync(id);
        }

        if (!Visible)
            _idCargado = null;

        return Task.CompletedTask;
    }

    private Task CambiarPestanaAsync(string pestana)
    {
        _pestanaActiva = pestana;
        return Task.CompletedTask;
    }

    private async Task CargarInformacionAsync(Guid vehiculoId)
    {
        var cancelacion = _cancelacion;
        var generacion = _generacion;
        _cargando = true;
        StateHasChanged();

        try
        {
            var detalle = await Mediator.Send(new ObtenerVehiculoPorIdQuery(vehiculoId), cancelacion);
            if (generacion != _generacion) return;
            _detalle = detalle;
        }
        catch (OperationCanceledException) when (cancelacion.IsCancellationRequested) { }
        finally
        {
            if (generacion == _generacion)
            {
                _cargando = false;
                StateHasChanged();
            }
        }
    }

    private Task Cerrar() => VisibleChanged.InvokeAsync(false);

    private async Task Operar(string pestana)
    {
        if (VehiculoId is not { } id) return;
        await VisibleChanged.InvokeAsync(false);
        await OnOperar.InvokeAsync((id, pestana));
    }

    public void Dispose()
    {
        _generacion++;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }
}

using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Trabajadores.Components;

public partial class TrabajadorPreviewDrawer : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;

    [Parameter] public Guid? TrabajadorId { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }

    private string _pestanaActiva = "informacion";
    private Guid? _idCargado;

    private TrabajadorDetalleDto? _detalle;
    private bool _cargando;

    protected override Task OnParametersSetAsync()
    {
        if (Visible && TrabajadorId is { } id && _idCargado != id)
        {
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

    private async Task CargarInformacionAsync(Guid trabajadorId)
    {
        _cargando = true;
        StateHasChanged();

        _detalle = await Mediator.Send(new ObtenerTrabajadorPorIdQuery(trabajadorId));
        _cargando = false;
        StateHasChanged();
    }

    private Task Cerrar() => VisibleChanged.InvokeAsync(false);

    // A diferencia de Cliente/Empresa (donde "Operar →" abre el
    // ContextWorkspace), Trabajador ya tiene su propia página completa
    // (/trabajadores/{id}, Trabajador 360) — es ESA la que remite al panel
    // de edición cuando hace falta, mismo patrón ya establecido en Centro
    // 360. Duplicar el panel aquí sería un tercer destino, no uno nuevo.
    //
    // forceLoad: true — este componente vive anidado dentro de la propia
    // página /trabajadores que la navegación va a reemplazar; un
    // NavigateTo normal (navegación mejorada, sin recarga) dispara el
    // cambio de ruta desde dentro del propio árbol de componentes que está
    // a punto de desmontarse, y en E2E real (Playwright) eso dejaba a
    // veces la URL actualizada pero el contenido de la lista todavía en
    // pantalla. Una recarga completa evita la carrera por completo — es
    // una transición ocasional lista→ficha, no una ruta caliente.
    private void AbrirTrabajador360()
    {
        if (TrabajadorId is not { } id) return;
        NavigationManager.NavigateTo($"/trabajadores/{id}", forceLoad: true);
    }
}

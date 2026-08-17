using CaeManager.Application.Clientes.Queries.ObtenerResumenCliente;
using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Infrastructure.Identity;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Clientes.Components;

public partial class ClientePreviewDrawer : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;

    [Parameter] public Guid? ClienteId { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<(Guid Id, string Pestana)> OnOperar { get; set; }

    private string _pestanaActiva = "informacion";
    private Guid? _clienteIdCargado;

    private ResumenClienteDto? _resumen;
    private bool _cargandoResumen;
    private ContactoAgendaDto? _contactoPrincipal;
    private bool _cargandoContacto;
    private string? _nombreEjecutivo;

    // Reabrir sobre un Cliente distinto (fila B tras fila A sin cerrar el
    // drawer) debe recargar todo desde cero — por eso se compara ClienteId
    // aquí en vez de solo mirar Visible. Documentación/Historial no
    // necesitan estado propio: PestanaDocumentacion/PestanaHistorial ya
    // recargan solas cuando cambia su parámetro de propietario.
    protected override Task OnParametersSetAsync()
    {
        if (Visible && ClienteId is { } id && _clienteIdCargado != id)
        {
            _clienteIdCargado = id;
            _pestanaActiva = "informacion";
            _resumen = null;
            _contactoPrincipal = null;
            _nombreEjecutivo = null;
            return CargarInformacionAsync(id);
        }

        if (!Visible)
            _clienteIdCargado = null;

        return Task.CompletedTask;
    }

    private Task CambiarPestanaAsync(string pestana)
    {
        _pestanaActiva = pestana;
        return Task.CompletedTask;
    }

    private async Task CargarInformacionAsync(Guid clienteId)
    {
        _cargandoResumen = true;
        _cargandoContacto = true;
        StateHasChanged();

        _resumen = await Mediator.Send(new ObtenerResumenClienteQuery(clienteId));
        _cargandoResumen = false;

        if (_resumen?.EjecutivoUsuarioId is { } ejecutivoId)
        {
            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                var usuario = await UserManager.FindByIdAsync(ejecutivoId.ToString());
                _nombreEjecutivo = usuario?.NombreCompleto ?? usuario?.Email;
            });
        }

        var agenda = await Mediator.Send(new ObtenerAgendaContactosQuery(TipoPropietarioAgenda.Cliente, clienteId));
        _contactoPrincipal = agenda.Count > 0 ? agenda[0] : null;
        _cargandoContacto = false;
        StateHasChanged();
    }

    private Task Cerrar() => VisibleChanged.InvokeAsync(false);

    private async Task Operar(string pestana)
    {
        if (ClienteId is not { } id) return;
        await VisibleChanged.InvokeAsync(false);
        await OnOperar.InvokeAsync((id, pestana));
    }
}

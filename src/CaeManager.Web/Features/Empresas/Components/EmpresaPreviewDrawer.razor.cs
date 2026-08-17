using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Application.Empresas.Queries.ObtenerCumplimientoEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresaPorId;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadores;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Empresas.Components;

public partial class EmpresaPreviewDrawer : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;

    [Parameter] public Guid? EmpresaId { get; set; }
    [Parameter] public bool Visible { get; set; }
    [Parameter] public EventCallback<bool> VisibleChanged { get; set; }
    [Parameter] public EventCallback<(Guid Id, string Pestana)> OnOperar { get; set; }

    private string _pestanaActiva = "informacion";
    private Guid? _idCargado;

    private EmpresaDetalleDto? _detalle;
    private bool _cargando;
    private int? _cumplimiento;
    private int _totalTrabajadores;
    private ContactoAgendaDto? _contactoPrincipal;
    private bool _cargandoContacto;

    protected override Task OnParametersSetAsync()
    {
        if (Visible && EmpresaId is { } id && _idCargado != id)
        {
            _idCargado = id;
            _pestanaActiva = "informacion";
            _detalle = null;
            _contactoPrincipal = null;
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

    private async Task CargarInformacionAsync(Guid empresaId)
    {
        _cargando = true;
        _cargandoContacto = true;
        StateHasChanged();

        _detalle = await Mediator.Send(new ObtenerEmpresaPorIdQuery(empresaId));
        _cumplimiento = await Mediator.Send(new ObtenerCumplimientoEmpresaQuery(empresaId));

        var trabajadores = await Mediator.Send(new ObtenerTrabajadoresQuery(null, EmpresaId: empresaId, TamanoPagina: 1));
        _totalTrabajadores = trabajadores.TotalElementos;
        _cargando = false;

        var agenda = await Mediator.Send(new ObtenerAgendaContactosQuery(TipoPropietarioAgenda.Empresa, empresaId));
        _contactoPrincipal = agenda.Count > 0 ? agenda[0] : null;
        _cargandoContacto = false;
        StateHasChanged();
    }

    private Task Cerrar() => VisibleChanged.InvokeAsync(false);

    private async Task Operar(string pestana)
    {
        if (EmpresaId is not { } id) return;
        await VisibleChanged.InvokeAsync(false);
        await OnOperar.InvokeAsync((id, pestana));
    }
}

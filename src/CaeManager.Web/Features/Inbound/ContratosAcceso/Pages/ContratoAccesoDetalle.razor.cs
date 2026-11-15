using CaeManager.Application.Inbound.ContratosAcceso.Commands.CancelarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.CerrarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Commands.EliminarContratoAcceso;
using CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerContratoAccesoPorId;
using CaeManager.Domain.Common;
using CaeManager.Domain.Inbound.ContratosAcceso;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Recursos;
using CaeManager.Web.Features.Inbound.ContratosAcceso.Servicios;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.Inbound.ContratosAcceso.Pages;

/// <summary>
/// Detalle de un contrato de acceso (tarjeta 11 de Inbound). Una lectura (<see cref="ObtenerContratoAccesoPorIdQuery"/>) y las
/// acciones de B-05: renovar, fijar condiciones, finalizar, cancelar y eliminar. Cada acción lleva la <c>Version</c> leída, así
/// que un cambio concurrente la rechaza el backend y la pantalla enseña su mensaje. La autoridad es la de cada Command; los
/// botones solo se ofrecen en los estados donde la transición tiene sentido (el dominio sigue siendo quien la decide).
/// </summary>
public partial class ContratoAccesoDetalle
{
    private enum AccionContrato
    {
        Ninguna,
        Finalizar,
        Cancelar,
        Eliminar,
    }

    [Parameter] public Guid Id { get; set; }

    /// <summary>Viene del botón «Editar» del listado: abre la edición de condiciones al cargar.</summary>
    [SupplyParameterFromQuery(Name = "editar")] public bool? Editar { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ResolutorContratosAcceso Resolutor { get; set; } = default!;
    [Inject] private IActividadDeContratoAcceso Actividad { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private ContratoAccesoFila? _fila;
    private ActividadDeContrato? _actividad;
    private IReadOnlyList<PestanaDefinicion> _pestanas = [];
    private string _pestanaActiva = PestanaSolicitudes;
    private bool _cargando = true;
    private bool _noEncontrado;
    private bool _errorCarga;
    private bool _renovando;
    private bool _editandoCondiciones;
    private bool _editarYaAbierto;
    private AccionContrato _accion = AccionContrato.Ninguna;
    private bool _enProgreso;
    private string? _errorAccion;
    private Guid _idCargado;

    private const string PestanaSolicitudes = "Solicitudes";
    private const string PestanaTrabajadores = "Trabajadores";
    private const string PestanaEmpresas = "Empresas";
    private const string PestanaRequerimientos = "Requerimientos";

    private IReadOnlyList<BreadcrumbElemento> Miguero =>
        [new BreadcrumbElemento(Textos["TituloPagina"]), new BreadcrumbElemento(_fila?.CentroNombre ?? "…")];

    private EstadoContratoAcceso? Estado => _fila?.Contrato.Estado;

    private bool PuedeRenovar => Estado is EstadoContratoAcceso.Activo or EstadoContratoAcceso.Suspendido && _fila?.Contrato.FechaFin is not null;

    // FijarCondiciones solo en un contrato Pendiente de confirmar o Activo (comentario de FijarCondicionesContratoAccesoCommand).
    private bool PuedeEditarCondiciones => Estado is EstadoContratoAcceso.PendienteDeConfirmar or EstadoContratoAcceso.Activo;

    private bool PuedeFinalizar => Estado is EstadoContratoAcceso.Activo or EstadoContratoAcceso.Suspendido;

    // Cancelar es para un contrato que aún no empezó (el dominio lo exige); uno que ya empezó se finaliza.
    private bool PuedeCancelar =>
        Estado is EstadoContratoAcceso.PendienteDeConfirmar or EstadoContratoAcceso.Activo or EstadoContratoAcceso.Suspendido
        && _fila is { } f && f.Contrato.FechaInicio > DiaDeNegocio.Hoy();

    // DN1 (DECISION_LOG): un contrato solo se elimina si está Cancelado o no ha empezado.
    private bool PuedeEliminar =>
        Estado == EstadoContratoAcceso.Cancelado || (_fila is { } f && f.Contrato.FechaInicio > DiaDeNegocio.Hoy());

    private string IconoPestana => _pestanaActiva switch
    {
        PestanaTrabajadores => "trabajadores",
        PestanaEmpresas => "empresas",
        PestanaRequerimientos => "documentos",
        _ => "documentos",
    };

    private string TituloVacioPestana => _pestanaActiva switch
    {
        PestanaTrabajadores => Textos["VacioTrabajadoresTitulo"],
        PestanaEmpresas => Textos["VacioEmpresasTitulo"],
        PestanaRequerimientos => Textos["VacioRequerimientosTitulo"],
        _ => Textos["VacioSolicitudesTitulo"],
    };

    private string DescripcionVacioPestana => _pestanaActiva switch
    {
        PestanaTrabajadores => Textos["VacioTrabajadoresDescripcion"],
        PestanaEmpresas => Textos["VacioEmpresasDescripcion"],
        PestanaRequerimientos => Textos["VacioRequerimientosDescripcion"],
        _ => Textos["VacioSolicitudesDescripcion"],
    };

    private string TituloAccion => _accion switch
    {
        AccionContrato.Finalizar => Textos["FinalizarTitulo"],
        AccionContrato.Cancelar => Textos["CancelarContratoTitulo"],
        _ => Textos["EliminarTitulo"],
    };

    private string MensajeAccion
    {
        get
        {
            var empresa = _fila?.EmpresaNombre ?? Textos["EmpresaNoDisponible"].Value;
            return _accion switch
            {
                AccionContrato.Finalizar => Textos["FinalizarMensaje", empresa, ContratoAccesoUi.Fecha(DiaDeNegocio.Hoy())],
                AccionContrato.Cancelar => Textos["CancelarContratoMensaje", empresa],
                _ => Textos["EliminarMensaje", empresa],
            };
        }
    }

    private string TextoConfirmarAccion => _accion switch
    {
        AccionContrato.Finalizar => Textos["BotonFinalizar"],
        AccionContrato.Cancelar => Textos["BotonCancelarContrato"],
        _ => Textos["BotonEliminar"],
    };

    protected override async Task OnParametersSetAsync()
    {
        if (_pestanas.Count == 0)
        {
            _pestanas =
            [
                new PestanaDefinicion(PestanaSolicitudes, Textos["PestanaSolicitudes"]) { Proximamente = true },
                new PestanaDefinicion(PestanaTrabajadores, Textos["PestanaTrabajadores"]) { Proximamente = true },
                new PestanaDefinicion(PestanaEmpresas, Textos["PestanaEmpresas"]) { Proximamente = true },
                new PestanaDefinicion(PestanaRequerimientos, Textos["PestanaRequerimientos"]) { Proximamente = true },
            ];
        }

        if (_idCargado != Id)
        {
            _idCargado = Id;
            await CargarAsync();
        }
    }

    private async Task CargarAsync()
    {
        _cargando = true;
        _noEncontrado = false;
        _errorCarga = false;
        try
        {
            var contrato = await Mediator.Send(new ObtenerContratoAccesoPorIdQuery(Id));
            if (contrato is null)
            {
                // Fuera de alcance o inexistente: la Query devuelve null y la página no distingue una cosa de la otra.
                _noEncontrado = true;
                _fila = null;
                return;
            }

            _fila = (await Resolutor.ResolverAsync([contrato])).FirstOrDefault();
            _actividad = (await Actividad.ObtenerAsync([contrato.Id])).TryGetValue(contrato.Id, out var actividad) ? actividad : null;

            if (Editar == true && !_editarYaAbierto && PuedeEditarCondiciones)
            {
                _editarYaAbierto = true;
                _editandoCondiciones = true;
            }
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private void CambiarPestana(string id) => _pestanaActiva = id;

    private void IrABreadcrumb(int indice)
    {
        if (indice == 0)
            Navegacion.NavigateTo("/inbound/contratos");
    }

    private async Task RenovadoAsync(DateOnly nuevaFecha)
    {
        _renovando = false;
        ToastService.Mostrar(Textos["ToastRenovado", ContratoAccesoUi.Fecha(nuevaFecha)], TonoToast.Exito);
        await CargarAsync();
    }

    private async Task CondicionesGuardadasAsync()
    {
        _editandoCondiciones = false;
        ToastService.Mostrar(Textos["ToastCondiciones"], TonoToast.Exito);
        await CargarAsync();
    }

    private void PedirAccion(AccionContrato accion)
    {
        _errorAccion = null;
        _accion = accion;
    }

    private void CerrarAccion(bool visible)
    {
        if (!visible && !_enProgreso)
            _accion = AccionContrato.Ninguna;
    }

    private async Task ConfirmarAccionAsync()
    {
        if (_fila is not { } fila)
            return;

        var contrato = fila.Contrato;
        _enProgreso = true;
        _errorAccion = null;
        try
        {
            Result resultado = _accion switch
            {
                AccionContrato.Finalizar => await Mediator.Send(new CerrarContratoAccesoCommand(contrato.Id, DiaDeNegocio.Hoy(), contrato.Version)),
                AccionContrato.Cancelar => await Mediator.Send(new CancelarContratoAccesoCommand(contrato.Id, contrato.Version)),
                _ => await Mediator.Send(new EliminarContratoAccesoCommand(contrato.Id)),
            };

            if (!resultado.EsExitoso)
            {
                _errorAccion = resultado.Error.Mensaje;
                return;
            }

            var hecha = _accion;
            _accion = AccionContrato.Ninguna;
            if (hecha == AccionContrato.Eliminar)
            {
                ToastService.Mostrar(Textos["ToastEliminado"], TonoToast.Exito);
                Navegacion.NavigateTo("/inbound/contratos");
                return;
            }

            ToastService.Mostrar(hecha == AccionContrato.Finalizar ? Textos["ToastFinalizado"] : Textos["ToastCancelado"], TonoToast.Exito);
            await CargarAsync();
        }
        catch (Exception)
        {
            _errorAccion = Textos["ErrorAccion"];
        }
        finally
        {
            _enProgreso = false;
        }
    }
}

using CaeManager.Application.Common;
using CaeManager.Application.Inbound.Requisitos.Commands.RetirarRequisitoPlantilla;
using CaeManager.Application.Inbound.Requisitos.Queries;
using CaeManager.Domain.Inbound.Requisitos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Services;
using CaeManager.Web.Features.Inbound.Plantillas.Servicios;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.Inbound.Plantillas.Pages;

/// <summary>
/// Requisitos de una plantilla y su edición (tarjeta 9 de Inbound). Una sola lectura (<see cref="ObtenerPlantillaRequisitosPorIdQuery"/>)
/// con la <c>Version</c> que cada comando devuelve tal cual: ante un conflicto se vuelve a leer la plantilla.
/// </summary>
public partial class RequisitosPlantilla
{
    [Parameter] public Guid Id { get; set; }

    /// <summary><c>?anadir=true</c> abre el panel para añadir un requisito (enlace de «Añadir» del listado).</summary>
    [SupplyParameterFromQuery(Name = "anadir")] public bool Anadir { get; set; }

    /// <summary><c>?editar={requisito}</c> abre el panel de ese requisito (enlace de «Editar» del listado).</summary>
    [SupplyParameterFromQuery(Name = "editar")] public Guid? Editar { get; set; }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ResolutorPlantillasRequisitos Resolutor { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;

    private readonly SerieDeLecturas _serie = new();
    private PlantillaRequisitosDto? _plantilla;
    private ReferenciasPlantillas _refs = new(
        new Dictionary<Guid, string>(), new Dictionary<Guid, string>(), new Dictionary<Guid, string>(),
        new Dictionary<Guid, string>(), new Dictionary<Guid, Application.TiposDocumento.Queries.ObtenerTiposDocumento.TipoDocumentoListaDto>());

    private bool _cargando = true;
    private bool _errorCarga;
    private Guid _idConError;
    private bool _drawerVisible;
    private bool _aperturaPorEnlaceHecha;
    private RequisitoPlantillaDto? _editando;
    private RequisitoPlantillaDto? _retirando;
    private bool _enProgreso;
    private string? _errorRetirar;

    private IReadOnlyList<RequisitoPlantillaDto> Activos => _plantilla?.Requisitos.Where(r => r.Activo).ToList() ?? [];

    private string NombrePlantilla => _plantilla is null ? string.Empty
        : _plantilla.Tipo == TipoPlantillaRequisitos.ExigenciasClienteEmpresarial
            ? Textos["NombreExigencias", _refs.NombreDe(_plantilla.Tipo, _plantilla.ReferenciaId) ?? Textos["ReferenciaNoDisponible"].Value].Value
            : _refs.NombreDe(_plantilla.Tipo, _plantilla.ReferenciaId) ?? Textos["ReferenciaNoDisponible"].Value;

    private string KickerPlantilla => _plantilla is null ? Textos["Kicker"].Value : Textos["KickerPlantilla", NombrePlantilla].Value;

    protected override async Task OnParametersSetAsync()
    {
        // Con un error de carga previo, un Id nuevo sí se vuelve a leer; el mismo Id espera al botón «Reintentar».
        if (_plantilla?.Id == Id || (_errorCarga && Id == _idConError))
            return;
        await CargarAsync();
    }

    private async Task CargarAsync()
    {
        _cargando = _plantilla is null;
        _errorCarga = false;
        try
        {
            using (await _serie.TomarAsync(CancellationToken.None))
            {
                _refs = await Resolutor.CargarAsync();
                _plantilla = await Mediator.Send(new ObtenerPlantillaRequisitosPorIdQuery(Id));
            }

            AbrirPorEnlace();
        }
        catch (Exception)
        {
            _errorCarga = true;
            _idConError = Id;
        }
        finally
        {
            _cargando = false;
        }
    }

    // Los enlaces del listado traen ?anadir o ?editar: se atienden una sola vez, después de la primera carga.
    private void AbrirPorEnlace()
    {
        if (_aperturaPorEnlaceHecha || _plantilla is not { Activa: true })
            return;
        _aperturaPorEnlaceHecha = true;
        if (Editar is { } requisitoId && _plantilla.Requisitos.FirstOrDefault(r => r.Id == requisitoId && r.Activo && r.Origen != OrigenRequisito.LegalN1) is { } r)
            AbrirEditar(r);
        else if (Anadir)
            AbrirAlta();
    }

    private string NombreRequisito(RequisitoPlantillaDto r) =>
        _refs.Tipos.TryGetValue(r.TipoDocumentoId, out var tipo) ? tipo.Nombre : Textos["RequisitoNoDisponible"].Value;

    private string TextoContrataciones(RequisitoPlantillaDto r) =>
        string.Join(", ", PlantillaRequisitosUi.Contrataciones.Where(c => r.TiposContratacion.Incluye(c)).Select(c => Textos[PlantillaRequisitosUi.ClaveContratacion(c)].Value));

    private void AbrirAlta()
    {
        _editando = null;
        _drawerVisible = true;
    }

    private void AbrirEditar(RequisitoPlantillaDto requisito)
    {
        _editando = requisito;
        _drawerVisible = true;
    }

    private void CerrarDrawer(bool visible)
    {
        if (!visible)
            _drawerVisible = false;
    }

    private async Task GuardadoAsync(bool alta)
    {
        _drawerVisible = false;
        ToastService.Mostrar(Textos[alta ? "ToastRequisitoAnadido" : "ToastRequisitoGuardado"], TonoToast.Exito);
        await CargarAsync();
    }

    // Solo ante un conflicto de versión (el panel avisa con OnFallo): la plantilla cambió desde que se leyó.
    // Los demás errores se quedan dentro del panel, con lo escrito.
    private async Task RefrescarTrasFalloAsync(string mensaje)
    {
        // El panel parte de lo que el usuario leyó: se cierra para que lo reabra con lo vigente y no reenvíe a ciegas.
        _drawerVisible = false;
        await CargarAsync();
        ToastService.Mostrar(mensaje, TonoToast.Error);
    }

    private void CerrarRetirar(bool visible)
    {
        if (visible)
            return;
        _retirando = null;
        _errorRetirar = null;
    }

    private async Task RetirarAsync()
    {
        if (_retirando is not { } requisito || _plantilla is null)
            return;

        _enProgreso = true;
        _errorRetirar = null;
        try
        {
            var resultado = await Mediator.Send(new RetirarRequisitoPlantillaCommand(_plantilla.Id, requisito.Id, _plantilla.Version));
            if (!resultado.EsExitoso && resultado.Error.Codigo != ConcurrenciaOptimista.CodigoConflicto)
            {
                // Cualquier otro error se queda dentro del diálogo.
                _errorRetirar = resultado.Error.Mensaje;
                return;
            }

            if (!resultado.EsExitoso)
            {
                _retirando = null;
                _errorRetirar = null;
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                await CargarAsync();
                return;
            }

            _retirando = null;
            ToastService.Mostrar(Textos["ToastRequisitoRetirado"], TonoToast.Exito);
            await CargarAsync();
        }
        catch (Exception)
        {
            _errorRetirar = Textos["ErrorGuardar"];
        }
        finally
        {
            _enProgreso = false;
        }
    }
}

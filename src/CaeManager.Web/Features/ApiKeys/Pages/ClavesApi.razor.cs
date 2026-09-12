using CaeManager.Application.ApiKeys.Commands.GenerarClaveApi;
using CaeManager.Application.ApiKeys.Commands.RevocarClaveApi;
using CaeManager.Application.ApiKeys.Queries.ObtenerClavesApi;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;

namespace CaeManager.Web.Features.ApiKeys.Pages;

public partial class ClavesApi : CaeManager.Web.Components.PaginaIntegrableConfiguracionBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<ClavesApi> Logger { get; set; } = default!;
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;
    private IReadOnlyList<DelegacionDto> _delegacionesSoporte = [];
    private bool _cargandoDelegaciones = true;
    private Guid? _delegacionSeleccionadaId;
    private IReadOnlyList<ClaveApiDto> _claves = [];
    private bool _cargandoClaves;
    private int _versionCarga;
    private int _generacionContexto;
    private bool _mostrarFormularioGenerar;
    private string _nombreNuevaClave = string.Empty;
    private bool _generando;
    private string? _errorGenerar;
    private string? _claveGenerada;
    private ClaveApiDto? _claveARevocar;
    private bool _revocando;
    private Guid? _procesandoId;

    private string MensajeRevocacion => _claveARevocar is null ? string.Empty : $"Se revocará la clave «{_claveARevocar.NombreDescriptivo}». No se puede deshacer; para volver a usar una integración habrá que generar una clave nueva.";

    protected override async Task OnInitializedAsync()
    {
        try { _delegacionesSoporte = (await Mediator.Send(new ObtenerDelegacionesQuery(), _ciclo.Token)).Where(d => d.EsSoporte && d.SomosLaConsultora).ToList(); }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested) { }
        catch (Exception ex) { Logger.LogError(ex, "Error al cargar las delegaciones de soporte disponibles."); ToastService.Mostrar("No pudimos cargar las organizaciones disponibles.", TonoToast.Error); }
        finally { if (!_desechado) _cargandoDelegaciones = false; }
    }

    private async Task OnDelegacionSeleccionadaAsync(string valor)
    {
        ReiniciarContexto();
        _delegacionSeleccionadaId = Guid.TryParse(valor, out var id) ? id : null;
        await CargarClavesAsync();
    }

    private void ReiniciarContexto()
    {
        _generacionContexto++; _versionCarga++; _claves = []; _cargandoClaves = false; _mostrarFormularioGenerar = false; _nombreNuevaClave = string.Empty; _generando = false; _errorGenerar = null; _claveGenerada = null; _claveARevocar = null; _revocando = false; _procesandoId = null;
    }

    private bool EsContextoVigente(int generacionContexto, Guid delegacionId) => !_desechado && _generacionContexto == generacionContexto && _delegacionSeleccionadaId == delegacionId;
    private string NombreOrganizacion(Guid delegacionId) => _delegacionesSoporte.FirstOrDefault(d => d.Id == delegacionId)?.ClienteNombre ?? "la organización original";

    private async Task CargarClavesAsync()
    {
        if (_delegacionSeleccionadaId is not { } delegacionId) return;
        var version = ++_versionCarga;
        _cargandoClaves = true;
        try
        {
            var claves = await Mediator.Send(new ObtenerClavesApiQuery(delegacionId), _ciclo.Token);
            if (_desechado || version != _versionCarga || _delegacionSeleccionadaId != delegacionId) return;
            _claves = claves;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (_desechado || version != _versionCarga || _delegacionSeleccionadaId != delegacionId) return;
            Logger.LogError(ex, "Error al cargar las claves API de la delegación {DelegacionId}.", delegacionId);
            ToastService.Mostrar("No pudimos cargar las claves de esta organización.", TonoToast.Error);
        }
        finally { if (!_desechado && version == _versionCarga && _delegacionSeleccionadaId == delegacionId) _cargandoClaves = false; }
    }

    private void AbrirGeneracion() { if (!_generando) { _errorGenerar = null; _mostrarFormularioGenerar = true; } }
    private void CambiarNombreNuevaClave(string valor) { _nombreNuevaClave = valor; _errorGenerar = null; }
    private void CerrarFormularioGenerar(bool visible) { if (!visible && !_generando) { _mostrarFormularioGenerar = false; _errorGenerar = null; } }
    private void CerrarClaveGenerada(bool visible) { if (!visible) _claveGenerada = null; }
    private void PedirRevocacion(ClaveApiDto clave) { if (!_revocando) _claveARevocar = clave; }
    private void CerrarRevocacion(bool visible) { if (!visible && !_revocando) _claveARevocar = null; }

    private async Task GenerarAsync()
    {
        if (_generando || _delegacionSeleccionadaId is not { } delegacionId) return;
        var generacionContexto = _generacionContexto;
        var nombreOrganizacion = NombreOrganizacion(delegacionId);
        _generando = true; _errorGenerar = null;
        try
        {
            var resultado = await Mediator.Send(new GenerarClaveApiCommand(delegacionId, _nombreNuevaClave));
            var fallido = resultado.EsFallido; var error = fallido ? resultado.Error.Mensaje : null;
            if (!EsContextoVigente(generacionContexto, delegacionId))
            {
                if (!_desechado && !fallido) ToastService.Mostrar($"Se generó una clave para «{nombreOrganizacion}», pero no se mostrará porque cambiaste de organización. Revócala y genera otra.", TonoToast.Advertencia);
                return;
            }
            if (fallido) { _errorGenerar = error; return; }
            _mostrarFormularioGenerar = false; _nombreNuevaClave = string.Empty; _claveGenerada = resultado.Valor.ClaveEnClaro;
            await CargarClavesAsync();
        }
        catch (ValidationException ex) { if (EsContextoVigente(generacionContexto, delegacionId)) _errorGenerar = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage)); }
        finally { if (EsContextoVigente(generacionContexto, delegacionId)) _generando = false; }
    }

    private async Task RevocarAsync()
    {
        if (_revocando || _claveARevocar is not { } clave || _delegacionSeleccionadaId is not { } delegacionId) return;
        var generacionContexto = _generacionContexto;
        var nombreOrganizacion = NombreOrganizacion(delegacionId);
        _revocando = true; _procesandoId = clave.Id;
        try
        {
            var resultado = await Mediator.Send(new RevocarClaveApiCommand(delegacionId, clave.Id));
            var fallido = resultado.EsFallido; var error = fallido ? resultado.Error.Mensaje : null;
            if (!EsContextoVigente(generacionContexto, delegacionId))
            {
                if (!_desechado) ToastService.Mostrar(fallido ? $"No se pudo revocar la clave en «{nombreOrganizacion}»: {error}" : $"Clave revocada en «{nombreOrganizacion}», pero no se actualizó la pantalla porque cambiaste de organización.", fallido ? TonoToast.Error : TonoToast.Advertencia);
                return;
            }
            if (fallido) { ToastService.Mostrar(error!, TonoToast.Error); return; }
            ToastService.Mostrar("Clave revocada.", TonoToast.Exito); _claveARevocar = null;
            await CargarClavesAsync();
        }
        catch (Exception ex) { if (EsContextoVigente(generacionContexto, delegacionId)) { Logger.LogError(ex, "Error al revocar la clave API {ClaveApiId}.", clave.Id); ToastService.Mostrar("No pudimos revocar la clave. Vuelve a cargar la lista para ver en qué estado quedó.", TonoToast.Error); } }
        finally { if (EsContextoVigente(generacionContexto, delegacionId)) { _revocando = false; _procesandoId = null; } }
    }

    public void Dispose() { _desechado = true; _claveGenerada = null; _nombreNuevaClave = null!; _claveARevocar = null; _ciclo.Cancel(); _ciclo.Dispose(); }
}

using CaeManager.Application.Tenants.Commands.RegistrarEncargoAdministracion;
using CaeManager.Application.Tenants.Commands.RetirarEncargoAdministracion;
using CaeManager.Application.Tenants.Queries.ObtenerEncargosAdministracion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Tenants;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.EncargoDeAdministracion.Recursos;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Localization;

namespace CaeManager.Web.Features.EncargoDeAdministracion;

public partial class EncargoAdministracionPanel : ComponentBase, IDisposable
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<EncargoAdministracionPanel> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosEncargoAdministracion> Textos { get; set; } = default!;

    /// <summary>
    /// Ofrece además registrar el encargo sobre las operaciones que aún no lo tienen. Solo cambia lo
    /// que se pinta: quién puede registrarlo lo decide <c>RegistrarEncargoAdministracionCommand</c>.
    /// </summary>
    [Parameter] public bool PermiteRegistrar { get; set; }

    private readonly CancellationTokenSource _ciclo = new();
    private IReadOnlyList<EncargoAdministracionDto> _encargos = [];
    private IReadOnlyList<OperacionEncargableDto> _encargables = [];
    private bool _cargando = true;
    private bool _error;
    private bool _desechado;

    private EncargoAdministracionDto? _aRetirar;
    private bool _retirando;

    private bool _drawerVisible;
    private OperacionEncargableDto? _operacionARegistrar;
    private string _clausula = string.Empty;
    private string? _errorClausula;
    private string? _mensajeErrorFormulario;
    private bool _guardando;

    private bool HayAlgoQueMostrar => _encargos.Count > 0 || (PermiteRegistrar && _encargables.Count > 0);
    private bool HayCambiosRegistro => !string.IsNullOrWhiteSpace(_clausula);

    private string TituloRetirada => Textos["RetirarTitulo", _aRetirar?.OperadorNombre ?? string.Empty];
    private string MensajeRetirada => Textos["RetirarMensaje", _aRetirar?.OperadorNombre ?? string.Empty];

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        try
        {
            // Un encargo retirado ya no gobierna nada: aquí solo se pinta lo que sigue sin retirar
            // (vigente o caducado; el caducado también se retira, porque bloquea registrar otro).
            var encargos = await Mediator.Send(new ObtenerEncargosAdministracionQuery(), _ciclo.Token);
            IReadOnlyList<OperacionEncargableDto> encargables = PermiteRegistrar
                ? await Mediator.Send(new ObtenerOperacionesEncargablesQuery(), _ciclo.Token)
                : [];
            if (_desechado) return;
            _encargos = encargos.Where(e => e.Estado != EstadoEncargoAdministracion.Retirado).ToList();
            _encargables = encargables;
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_desechado) return;
            Logger.LogError(ex, "Error al cargar el Encargo de administración.");
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private static TonoBadge TonoDe(EncargoAdministracionDto encargo) =>
        encargo.Estado == EstadoEncargoAdministracion.Vigente ? TonoBadge.Exito : TonoBadge.Advertencia;

    private string EstadoDe(EncargoAdministracionDto encargo) =>
        encargo.Estado == EstadoEncargoAdministracion.Vigente ? Textos["EstadoVigente"] : Textos["EstadoCaducado"];

    private string OrigenDe(EncargoAdministracionDto encargo) =>
        encargo.Origen == OrigenEncargoAdministracion.AprovisionamientoDePlataforma
            ? Textos["OrigenPlataforma"]
            : Textos["OrigenAdministradorPropio"];

    private string VigenciaDe(EncargoAdministracionDto encargo) =>
        encargo.VigenciaHasta is { } hasta ? Textos["VigenteHasta", Dia(hasta)] : Textos["SinFechaFin"];

    /// <summary>El día de negocio (España peninsular) del instante, con el formato corto de la cultura.</summary>
    private static string Dia(DateTime instanteUtc) => DiaDeNegocio.De(instanteUtc).ToString("d");

    // --- Retirar: pregunta antes -------------------------------------------------------------

    private void PedirRetirada(EncargoAdministracionDto encargo) => _aRetirar = encargo;

    private void CerrarRetirada(bool visible)
    {
        if (!visible && !_retirando)
            _aRetirar = null;
    }

    private async Task RetirarAsync()
    {
        if (_retirando || _aRetirar is not { } encargo) return;
        _retirando = true;
        try
        {
            var resultado = await Mediator.Send(new RetirarEncargoAdministracionCommand(encargo.Id), _ciclo.Token);
            if (_desechado) return;
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar(Textos["ToastRetirado"], TonoToast.Exito);
            _aRetirar = null;
            await CargarAsync();
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_desechado) return;
            Logger.LogError(ex, "Error al retirar el Encargo de administración {EncargoId}.", encargo.Id);
            ToastService.Mostrar(Textos["ErrorRetirar"], TonoToast.Error);
        }
        finally
        {
            _retirando = false;
        }
    }

    // --- Registrar: la cláusula del contrato es obligatoria ----------------------------------

    private void AbrirRegistro(OperacionEncargableDto operacion)
    {
        _operacionARegistrar = operacion;
        _clausula = string.Empty;
        _errorClausula = null;
        _mensajeErrorFormulario = null;
        _drawerVisible = true;
    }

    private void AlCambiarClausula(string valor)
    {
        _clausula = valor;
        _errorClausula = null;
    }

    private Task CerrarDrawerAsync(bool visible)
    {
        _drawerVisible = visible;
        return Task.CompletedTask;
    }

    private async Task GuardarAsync()
    {
        // Guarda de doble clic: un segundo «Registrar» antes de que vuelva el primero chocaría con
        // el índice de un solo encargo sin retirar por operación.
        if (_guardando || _operacionARegistrar is not { } operacion) return;
        _mensajeErrorFormulario = null;
        _errorClausula = null;

        if (string.IsNullOrWhiteSpace(_clausula))
        {
            _errorClausula = Textos["ClausulaObligatoria"];
            return;
        }

        if (_clausula.Trim().Length > EncargoAdministracion.LongitudMaximaClausula)
        {
            _errorClausula = Textos["ClausulaDemasiadoLarga", EncargoAdministracion.LongitudMaximaClausula];
            return;
        }

        _guardando = true;
        try
        {
            // Sin fecha de fin: el encargo dura lo que dure la operación, o hasta que se retire.
            var resultado = await Mediator.Send(
                new RegistrarEncargoAdministracionCommand(operacion.AsignacionOperacionId, _clausula, null), _ciclo.Token);
            if (_desechado) return;
            if (resultado.EsFallido)
            {
                _mensajeErrorFormulario = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar(Textos["ToastRegistrado"], TonoToast.Exito);
            _drawerVisible = false;
            await CargarAsync();
        }
        catch (OperationCanceledException) when (_ciclo.IsCancellationRequested)
        {
        }
        catch (ValidationException ex)
        {
            _errorClausula = ex.Errors.FirstOrDefault()?.ErrorMessage ?? Textos["ClausulaObligatoria"];
        }
        catch (Exception ex)
        {
            if (_desechado) return;
            Logger.LogError(ex, "Error al registrar el Encargo de administración.");
            _mensajeErrorFormulario = Textos["ErrorGuardar"];
        }
        finally
        {
            _guardando = false;
        }
    }

    public void Dispose()
    {
        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
        GC.SuppressFinalize(this);
    }
}

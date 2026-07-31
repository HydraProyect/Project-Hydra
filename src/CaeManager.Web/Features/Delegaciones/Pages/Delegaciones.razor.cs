using CaeManager.Application.Common;
using CaeManager.Application.Tenants.Commands.AbrirAccesoSoporte;
using CaeManager.Application.Tenants.Commands.CerrarAccesoSoporte;
using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Queries.ObtenerActividadSoporte;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Domain.Soporte;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentValidation;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Delegaciones.Pages;

/// <summary>
/// Administración de Delegated Workspaces (ADR-004). Cierra el hallazgo N-4
/// de INFORME-AUDITORIA-2.md: las delegaciones existían pero no se podían
/// revocar por ningún camino del producto, lo que contradice el titular del
/// propio ADR — un modelo de delegación reversible.
///
/// Solo revoca y reactiva. Dar de alta una delegación nueva sigue sin flujo
/// de producto a propósito: el ADR-004 § 12.2 deja abierto quién puede
/// iniciarla (¿el cliente, la consultora, ambos?) y lo marca como decisión
/// con implicaciones comerciales. Inventarlo aquí sería diseñar producto
/// bajo la excusa de cerrar una auditoría.
/// </summary>
public partial class Delegaciones : ComponentBase
{
    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private ToastService ToastService { get; set; } = default!;
    [Inject] private ILogger<Delegaciones> Logger { get; set; } = default!;
    [Inject] private IClienteActivoSeleccionado ClienteActivoSeleccionado { get; set; } = default!;

    /// <summary>
    /// Gestionar delegaciones se hace desde la propia organización, nunca
    /// operando el workspace de otra: los comandos deciden con el tenant de
    /// origen, así que desde aquí fallarían — y con un mensaje sobre el rol
    /// que no explica el motivo real.
    /// </summary>
    private bool OperandoWorkspaceAjeno => ClienteActivoSeleccionado.TenantIdSeleccionado is not null;

    private IReadOnlyList<DelegacionDto> _delegaciones = [];
    private readonly Dictionary<Guid, string> _nombresPorUsuarioId = [];
    private bool _cargando = true;
    private bool _error;

    private DelegacionDto? _delegacionARevocar;
    private bool _revocando;
    private Guid? _procesandoId;

    private DelegacionDto? _verActividadDe;
    private IReadOnlyList<ActividadSoporteDto> _actividad = [];
    private bool _cargandoActividad;

    private DelegacionDto? _delegacionSoporteAAbrir;
    private string _motivoSoporte = string.Empty;
    private string _diasSoporte = "7";
    private string _rolSoporte = RolesSoporte.SoloLectura;
    private bool _abriendoSoporte;
    private string? _errorSoporte;

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _error = false;
        StateHasChanged();

        try
        {
            _delegaciones = await Mediator.Send(new ObtenerDelegacionesQuery());
            await CargarNombresDeOperadoresAsync();
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar las delegaciones del tenant de origen.");
            _error = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    /// <summary>
    /// La query devuelve Guids de usuario, no nombres: <c>ApplicationUser</c>
    /// vive en Infrastructure.Identity y Application no puede referenciarlo
    /// (mismo motivo que <c>AsignacionOperadorDelegado.UsuarioId</c> es un
    /// Guid suelto). Se resuelven aquí, que es la capa que sí lo conoce.
    /// </summary>
    private async Task CargarNombresDeOperadoresAsync()
    {
        foreach (var usuarioId in _delegaciones.SelectMany(d => d.Operadores).Select(o => o.UsuarioId).Distinct())
        {
            if (_nombresPorUsuarioId.ContainsKey(usuarioId)) continue;

            var usuario = await UserManager.FindByIdAsync(usuarioId.ToString());
            _nombresPorUsuarioId[usuarioId] = usuario is null
                ? "Usuario no encontrado"
                : $"{usuario.NombreCompleto} ({usuario.Email})";
        }
    }

    private string NombreDeUsuario(Guid usuarioId) =>
        _nombresPorUsuarioId.GetValueOrDefault(usuarioId, "…");

    private async Task RevocarAsync()
    {
        if (_delegacionARevocar is null) return;

        _revocando = true;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new DesactivarDelegacionTenantCommand(_delegacionARevocar.Id));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Acceso revocado.", TonoToast.Exito);
            _delegacionARevocar = null;
            await CargarAsync();
        }
        finally
        {
            _revocando = false;
        }
    }

    private async Task VerActividadAsync(DelegacionDto delegacion)
    {
        _verActividadDe = delegacion;
        _actividad = [];
        _cargandoActividad = true;
        StateHasChanged();

        try
        {
            _actividad = await Mediator.Send(new ObtenerActividadSoporteQuery(delegacion.Id));
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "Error al cargar la actividad de soporte de la delegación {DelegacionId}.", delegacion.Id);
            ToastService.Mostrar("No pudimos cargar la actividad registrada.", TonoToast.Error);
        }
        finally
        {
            _cargandoActividad = false;
        }
    }

    private static string DescribirTipo(TipoActividadSoporte tipo) => tipo switch
    {
        TipoActividadSoporte.AccesoConcedido => "Acceso concedido",
        TipoActividadSoporte.WorkspaceActivado => "Entró en la organización",
        TipoActividadSoporte.Navegacion => "Navegó a",
        TipoActividadSoporte.Interaccion => "Pulsó",
        TipoActividadSoporte.AccesoRevocado => "Acceso cerrado",
        _ => tipo.ToString()
    };

    private void AbrirFormularioSoporte(DelegacionDto delegacion)
    {
        _delegacionSoporteAAbrir = delegacion;
        _motivoSoporte = string.Empty;
        _diasSoporte = "7";
        _rolSoporte = RolesSoporte.SoloLectura;
        _errorSoporte = null;
    }

    private async Task AbrirAccesoSoporteAsync()
    {
        if (_delegacionSoporteAAbrir is null) return;

        _abriendoSoporte = true;
        _errorSoporte = null;
        StateHasChanged();

        try
        {
            if (!int.TryParse(_diasSoporte, out var dias))
            {
                _errorSoporte = "Indica los días de acceso como un número.";
                return;
            }

            var resultado = await Mediator.Send(
                new AbrirAccesoSoporteCommand(_delegacionSoporteAAbrir.Id, _motivoSoporte, dias, _rolSoporte));

            if (resultado.EsFallido)
            {
                _errorSoporte = resultado.Error.Mensaje;
                return;
            }

            ToastService.Mostrar("Acceso de soporte abierto.", TonoToast.Exito);
            _delegacionSoporteAAbrir = null;
            await CargarAsync();
        }
        catch (ValidationException ex)
        {
            _errorSoporte = string.Join(" ", ex.Errors.Select(e => e.ErrorMessage));
        }
        finally
        {
            _abriendoSoporte = false;
        }
    }

    private async Task CerrarAccesoSoporteAsync(DelegacionDto delegacion)
    {
        _procesandoId = delegacion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new CerrarAccesoSoporteCommand(delegacion.Id));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Acceso de soporte cerrado.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    private async Task ReactivarAsync(DelegacionDto delegacion)
    {
        _procesandoId = delegacion.Id;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ReactivarDelegacionTenantCommand(delegacion.Id));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Delegación reactivada.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }

    private async Task RetirarOperadorAsync(OperadorDelegadoDto operador)
    {
        _procesandoId = operador.AsignacionId;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new RevocarAsignacionOperadorDelegadoCommand(operador.AsignacionId));

            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            ToastService.Mostrar("Operador retirado de la delegación.", TonoToast.Exito);
            await CargarAsync();
        }
        finally
        {
            _procesandoId = null;
        }
    }
}

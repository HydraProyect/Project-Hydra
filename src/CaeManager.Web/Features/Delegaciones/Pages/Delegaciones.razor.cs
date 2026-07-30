using CaeManager.Application.Tenants.Commands.DesactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.ReactivarDelegacionTenant;
using CaeManager.Application.Tenants.Commands.RevocarAsignacionOperadorDelegado;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
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

    private IReadOnlyList<DelegacionDto> _delegaciones = [];
    private readonly Dictionary<Guid, string> _nombresPorUsuarioId = [];
    private bool _cargando = true;
    private bool _error;

    private DelegacionDto? _delegacionARevocar;
    private bool _revocando;
    private Guid? _procesandoId;

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
        catch (Exception)
        {
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

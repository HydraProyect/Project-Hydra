using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using MediatR;

namespace CaeManager.Application.Usuarios.Queries.ObtenerRestablecimientoPorSoporte;

/// <summary>
/// Lo que la pantalla de Soporte TALVEG necesita para ofrecer el restablecimiento
/// de la verificación en dos pasos (ADR-011 § 8.7, punto 3).
/// </summary>
/// <param name="Administrador">El único Administrador activo del Tenant objetivo, o
/// <c>null</c> si no hay ninguno o hay varios: en ese caso Soporte TALVEG no
/// restablece, lo hace un Administrador desde Usuarios.</param>
public record RestablecimientoPorSoporteDto(AdministradorUnicoActivo? Administrador);

/// <summary>
/// <c>null</c> salvo dentro de una Sesión Privilegiada con la capacidad
/// <c>RestablecimientoSegundoFactor</c>, sin simulación y con el Tenant operado igual
/// al Tenant objetivo: fuera de ese caso la sección no existe. No autoriza nada: la
/// acción la decide <c>RestablecerSegundoFactorCommand</c> y la vuelve a comprobar la
/// base. Solo expone a quién se restablecería, porque el técnico tiene que poder
/// confirmarlo con quien pide ayuda.
/// </summary>
public record ObtenerRestablecimientoPorSoporteQuery : IRequest<RestablecimientoPorSoporteDto?>;

public class ObtenerRestablecimientoPorSoporteQueryHandler(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual,
    ISegundoFactorDeCuentas segundoFactor)
    : IRequestHandler<ObtenerRestablecimientoPorSoporteQuery, RestablecimientoPorSoporteDto?>
{
    public async Task<RestablecimientoPorSoporteDto?> Handle(
        ObtenerRestablecimientoPorSoporteQuery request, CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not { } sesion
            || sesion.Capacidad != CapacidadPrivilegio.RestablecimientoSegundoFactor
            || sesion.UsuarioSimuladoId is not null
            || tenantActual.TenantId != sesion.TenantObjetivoId)
            return null;

        return new RestablecimientoPorSoporteDto(
            await segundoFactor.ObtenerAdministradorUnicoActivoAsync(sesion.TenantObjetivoId, cancellationToken));
    }
}

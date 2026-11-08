using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Domain.Plataforma;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Queries.ObtenerAjusteSegundoFactorPorPlataforma;

/// <summary>Lo que la pantalla de Plataforma necesita para ofrecer el cambio: el valor vigente.</summary>
public record AjusteSegundoFactorPorPlataformaDto(bool ExigeSegundoFactorTodosLosPerfiles);

/// <summary>
/// <c>null</c> salvo dentro de una Sesión Privilegiada con la capacidad
/// <c>AjusteSegundoFactorTenant</c>, sin simulación y con el Tenant operado igual al Tenant
/// objetivo: fuera de ese caso la sección no existe. No autoriza nada: el cambio lo decide
/// <c>CambiarSegundoFactorDelTenantPorPlataformaCommand</c> y lo vuelve a comprobar la base.
/// Revalida la sesión como el comando: con la memo de <c>ObtenerAsync</c>, una concesión
/// revocada en otra pestaña seguiría mostrando el estado del segundo factor del Tenant
/// hasta la siguiente revalidación del circuito.
/// </summary>
public record ObtenerAjusteSegundoFactorPorPlataformaQuery : IRequest<AjusteSegundoFactorPorPlataformaDto?>;

public class ObtenerAjusteSegundoFactorPorPlataformaQueryHandler(
    ISesionPrivilegiadaActual sesionPrivilegiadaActual,
    ITenantActual tenantActual,
    IConfiguracionQueryContext configuracion)
    : IRequestHandler<ObtenerAjusteSegundoFactorPorPlataformaQuery, AjusteSegundoFactorPorPlataformaDto?>
{
    public async Task<AjusteSegundoFactorPorPlataformaDto?> Handle(
        ObtenerAjusteSegundoFactorPorPlataformaQuery request, CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.RevalidarAsync(cancellationToken) is not { } sesion
            || sesion.Capacidad != CapacidadPrivilegio.AjusteSegundoFactorTenant
            || sesion.UsuarioSimuladoId is not null
            || tenantActual.TenantId != sesion.TenantObjetivoId)
            return null;

        var exige = await configuracion.ParametrosSistema
            .Select(p => p.ExigeSegundoFactorTodosLosPerfiles)
            .SingleAsync(cancellationToken);
        return new AjusteSegundoFactorPorPlataformaDto(exige);
    }
}

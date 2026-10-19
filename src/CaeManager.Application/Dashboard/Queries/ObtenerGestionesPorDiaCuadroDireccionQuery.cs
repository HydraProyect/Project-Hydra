using CaeManager.Application.Common;
using CaeManager.Application.Gestiones;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Gestiones completadas por día natural (hora peninsular) en los <paramref name="Meses"/> meses que terminan en
/// (<paramref name="Anyo"/>, <paramref name="Mes"/>): alimenta «Gestiones por día» de la pestaña Demanda. Cuenta
/// cuándo se completó la gestión (<c>CompletadaEnUtc</c>), nunca quién la completó.
/// </summary>
public record ObtenerGestionesPorDiaCuadroDireccionQuery(int Anyo, int Mes, int Meses)
    : IRequest<GestionesPorDiaCuadroDireccionDto>;

public record GestionesDelDiaDto(DateOnly Dia, int Cantidad);

public record GestionesPorDiaTenantDto(Guid TenantId, string TenantNombre, IReadOnlyList<GestionesDelDiaDto> Dias);

public record GestionesPorDiaCuadroDireccionDto(
    IReadOnlyList<GestionesPorDiaTenantDto> Tenants, int TenantsSinAlcanceCompleto)
{
    /// <summary>Suma de todos los Tenants, o solo de uno si se pasa su identificador.</summary>
    public IReadOnlyList<GestionesDelDiaDto> Sumar(Guid? tenantId = null) =>
        Tenants.Where(t => tenantId is null || t.TenantId == tenantId)
            .SelectMany(t => t.Dias)
            .GroupBy(d => d.Dia)
            .Select(g => new GestionesDelDiaDto(g.Key, g.Sum(d => d.Cantidad)))
            .OrderBy(d => d.Dia)
            .ToList();
}

public class ObtenerGestionesPorDiaCuadroDireccionQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    IGestionesQueryContext gestionesContext,
    ICurrentUserService currentUserService,
    TimeProvider reloj)
    : IRequestHandler<ObtenerGestionesPorDiaCuadroDireccionQuery, GestionesPorDiaCuadroDireccionDto>
{
    public async Task<GestionesPorDiaCuadroDireccionDto> Handle(ObtenerGestionesPorDiaCuadroDireccionQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(currentUserService);
        var meses = ObtenerSerieCuadroDireccionQueryHandler.MesesDeLaSerie(request.Anyo, request.Mes, request.Meses, reloj.GetUtcNow().UtcDateTime);
        if (meses.Count == 0)
            return new GestionesPorDiaCuadroDireccionDto([], 0);

        var desde = meses[0].InicioUtc;
        var hasta = meses[^1].FinUtc;
        var clientes = await mediator.Send(new ObtenerClientesAutorizadosQuery(), cancellationToken);
        var porTenant = new List<GestionesPorDiaTenantDto>();
        var sinAlcance = 0;

        foreach (var cliente in clientes)
        {
            using (AmbitoTenantExplicito.Establecer(cliente.TenantId))
            {
                if (!await alcanceDatos.AlcanzaElTenantEnteroAsync(cancellationToken))
                {
                    sinAlcance++;
                    continue;
                }

                var completadas = await gestionesContext.Gestiones
                    .Where(g => g.CompletadaEnUtc >= desde && g.CompletadaEnUtc < hasta)
                    .Select(g => g.CompletadaEnUtc!.Value)
                    .ToListAsync(cancellationToken);

                porTenant.Add(new GestionesPorDiaTenantDto(cliente.TenantId, cliente.Nombre, PorDia(completadas)));
            }
        }

        return new GestionesPorDiaCuadroDireccionDto(porTenant.OrderBy(t => t.TenantNombre, StringComparer.CurrentCulture).ToList(), sinAlcance);
    }

    /// <summary>Agrupa instantes UTC por día de negocio (hora peninsular), del más antiguo al más reciente.</summary>
    public static IReadOnlyList<GestionesDelDiaDto> PorDia(IEnumerable<DateTime> instantesUtc) =>
        instantesUtc
            .GroupBy(i => DiaDeNegocio.De(DateTime.SpecifyKind(i, DateTimeKind.Utc)))
            .Select(g => new GestionesDelDiaDto(g.Key, g.Count()))
            .OrderBy(d => d.Dia)
            .ToList();
}

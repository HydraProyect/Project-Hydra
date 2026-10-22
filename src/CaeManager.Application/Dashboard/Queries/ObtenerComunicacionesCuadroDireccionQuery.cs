using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Dashboard.Queries;

/// <summary>
/// Lo que el Cuadro de dirección puede contar de Comunicaciones con el modelo que existe: las conversaciones
/// vivas y abiertas por estado (foto de hoy) y las solicitudes del mes por canal. Los tiempos de respuesta, la
/// intención CAE y los equipos no tienen modelo todavía: no se calculan ni se inventan.
/// </summary>
public record ObtenerComunicacionesCuadroDireccionQuery(int Anyo, int Mes) : IRequest<ComunicacionesCuadroDireccionDto>;

public record ConteoPorEstadoDto(EstadoConversacion Estado, int Conversaciones);

public record ConteoPorCanalDto(CanalConversacion Canal, int Solicitudes);

/// <param name="PorEstado">Conversaciones vivas y abiertas (ni resueltas ni cerradas) hoy, por estado; no se reconstruye para meses pasados.</param>
/// <param name="PorCanal">Solicitudes del mes (primer mensaje entrante de cada conversación) por canal.</param>
public record ComunicacionesTenantDto(
    Guid TenantId, string TenantNombre, IReadOnlyList<ConteoPorEstadoDto> PorEstado, IReadOnlyList<ConteoPorCanalDto> PorCanal);

public record ComunicacionesCuadroDireccionDto(
    int Anyo, int Mes, IReadOnlyList<ComunicacionesTenantDto> Tenants,
    IReadOnlyList<ConteoPorEstadoDto> TotalPorEstado, IReadOnlyList<ConteoPorCanalDto> TotalPorCanal,
    int TenantsSinAlcanceCompleto);

public class ObtenerComunicacionesCuadroDireccionQueryHandler(
    IMediator mediator,
    IAlcanceDatosService alcanceDatos,
    IComunicacionesQueryContext comunicacionesContext,
    ICurrentUserService currentUserService,
    IPoliticaNotaInterna politicaAcceso,
    TimeProvider reloj)
    : IRequestHandler<ObtenerComunicacionesCuadroDireccionQuery, ComunicacionesCuadroDireccionDto>
{
    /// <summary>Estados en los que una conversación sigue abierta: no cuentan las resueltas ni las cerradas.</summary>
    public static readonly EstadoConversacion[] EstadosAbiertos =
        [EstadoConversacion.Abierta, EstadoConversacion.Pendiente, EstadoConversacion.EsperandoRespuestaExterna, EstadoConversacion.EnEsperaDeTercero];

    public async Task<ComunicacionesCuadroDireccionDto> Handle(ObtenerComunicacionesCuadroDireccionQuery request, CancellationToken cancellationToken)
    {
        await AutorizacionCuadroDireccion.ExigirLecturaAsync(currentUserService, politicaAcceso);
        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        if (!MesCuadroDireccion.EsValido(request.Anyo, request.Mes) || new MesCuadroDireccion(request.Anyo, request.Mes).InicioUtc > ahoraUtc)
            return Fusionar(request.Anyo, request.Mes, [], 0);

        var mes = new MesCuadroDireccion(request.Anyo, request.Mes);
        var inicio = mes.InicioUtc;
        var fin = mes.FinUtc;
        var abiertos = EstadosAbiertos;
        var clientes = await mediator.Send(new ObtenerTenantsCuadroDireccionQuery(), cancellationToken);
        var porTenant = new List<ComunicacionesTenantDto>();
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

                var estados = await comunicacionesContext.Conversaciones
                    .Where(c => abiertos.Contains(c.Estado))
                    .GroupBy(c => c.Estado)
                    .Select(g => new ConteoPorEstadoDto(g.Key, g.Count()))
                    .ToListAsync(cancellationToken);

                // Solo conversaciones vivas; el primer entrante decide el mes (decisión 3 de 2026-10-17).
                var vivas = comunicacionesContext.Conversaciones.Select(c => c.Id);
                var solicitadas = await comunicacionesContext.Mensajes
                    .Where(m => m.Direccion == DireccionMensaje.Entrante && vivas.Contains(m.ConversacionId))
                    .GroupBy(m => m.ConversacionId)
                    .Select(g => new { Id = g.Key, Primero = g.Min(m => m.FechaUtc) })
                    .Where(x => x.Primero >= inicio && x.Primero < fin)
                    .Select(x => x.Id)
                    .ToListAsync(cancellationToken);

                var canales = solicitadas.Count == 0
                    ? []
                    : await comunicacionesContext.Conversaciones
                        .Where(c => solicitadas.Contains(c.Id))
                        .GroupBy(c => c.Canal)
                        .Select(g => new ConteoPorCanalDto(g.Key, g.Count()))
                        .ToListAsync(cancellationToken);

                porTenant.Add(new ComunicacionesTenantDto(cliente.TenantId, cliente.Nombre, estados, canales));
            }
        }

        return Fusionar(request.Anyo, request.Mes, porTenant, sinAlcance);
    }

    /// <summary>Fusión pura: suma por estado y por canal, y deja cada estado y canal con su recuento (también a cero).</summary>
    public static ComunicacionesCuadroDireccionDto Fusionar(
        int anyo, int mes, IReadOnlyList<ComunicacionesTenantDto> tenants, int tenantsSinAlcanceCompleto) =>
        new(anyo, mes,
            tenants.OrderBy(t => t.TenantNombre, StringComparer.CurrentCulture).ToList(),
            EstadosAbiertos
                .Select(e => new ConteoPorEstadoDto(e, tenants.Sum(t => t.PorEstado.Where(x => x.Estado == e).Sum(x => x.Conversaciones))))
                .ToList(),
            Enum.GetValues<CanalConversacion>()
                .Select(c => new ConteoPorCanalDto(c, tenants.Sum(t => t.PorCanal.Where(x => x.Canal == c).Sum(x => x.Solicitudes))))
                .ToList(),
            tenantsSinAlcanceCompleto);
}

using CaeManager.Application.Common;
using CaeManager.Application.Empresas;
using CaeManager.Domain.Blindaje42;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Blindaje42.Queries.ObtenerBlindajeEmpresasDeCliente;

/// <summary>
/// Respalda la pestaña "Blindaje 42.1" del Context Workspace de Cliente: una
/// fila por cada Empresa relacionada, con el estado de su última solicitud
/// de certificación TGSS. El estado es el de ESA solicitud concreta — no una
/// promesa de blindaje continuo, ver el comentario de
/// <see cref="SolicitudCertificacionTgss"/>.
///
/// Las filas son la UNIÓN de (a) Empresas con una <c>RelacionEmpresarial</c>
/// vigente hoy con este Cliente (mismo filtro que <c>ObtenerEmpresasDeClienteQuery</c>,
/// F4.2b) y (b) Empresas con solicitudes ya registradas aunque su relación
/// haya cerrado desde entonces — la responsabilidad solidaria del art. 42.2
/// ET sigue viva hasta 3 años después de terminar el encargo, así que ese
/// historial no puede desaparecer solo porque la relación ya no está activa.
///
/// Esa unión se filtra SIEMPRE por <c>ObtenerEmpresaIdsVisiblesAsync</c>
/// además del <c>ClienteVisibleAsync</c> de arriba: la cartera de Empresas de
/// <c>AlcanceDatosService</c> se deriva de relaciones VIGENTES (mismo cálculo
/// que aquí, ver <c>AlcanceDatosService.ObtenerEmpresaIdsVisiblesAsync</c>),
/// así que una Empresa cuya única relación con este Cliente ya cerró puede
/// quedar fuera de la cartera de un gestor con acceso restringido aunque el
/// Cliente le sea visible. Sin este filtro, la mitad (b) de la unión sería
/// una vía lateral para leer una Empresa fuera de cartera — exactamente el
/// agujero que <c>AlcanceDatosServiceExtensions</c> existe para cerrar.
/// </summary>
public record ObtenerBlindajeEmpresasDeClienteQuery(Guid ClienteId) : IRequest<IReadOnlyList<BlindajeEmpresaDto>>;

public record BlindajeEmpresaDto(
    Guid EmpresaId,
    string EmpresaRazonSocial,
    string? EmpresaCif,
    int TotalSolicitudes,
    UltimaSolicitudCertificacionTgssDto? UltimaSolicitud);

public record UltimaSolicitudCertificacionTgssDto(
    Guid Id,
    DateOnly FechaSolicitud,
    DateOnly FechaLimiteOrientativa,
    ResultadoCertificacionTgss? Resultado,
    DateOnly? FechaRespuesta,
    EstadoBlindaje42 Estado);

public class ObtenerBlindajeEmpresasDeClienteQueryHandler(
    IEmpresasQueryContext empresasContext, IBlindaje42QueryContext blindajeContext, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerBlindajeEmpresasDeClienteQuery, IReadOnlyList<BlindajeEmpresaDto>>
{
    public async Task<IReadOnlyList<BlindajeEmpresaDto>> Handle(
        ObtenerBlindajeEmpresasDeClienteQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.ClienteVisibleAsync(request.ClienteId, cancellationToken))
            return [];

        var solicitudes = await blindajeContext.SolicitudesCertificacionTgss
            .Where(s => s.ClienteId == request.ClienteId)
            .Select(s => new
            {
                s.Id,
                s.EmpresaId,
                s.FechaSolicitud,
                s.Resultado,
                s.FechaRespuesta,
                s.CreadoEnUtc
            })
            .ToListAsync(cancellationToken);

        var empresaIdsConRelacionVigente = await empresasContext.RelacionesEmpresariales
            .Where(r => r.ClienteId == request.ClienteId && r.VigenciaHasta == null)
            .Join(empresasContext.Empresas.Where(e => e.EsPropia), r => r.ProveedoraId, e => e.Id, (r, e) => e.Id)
            .ToListAsync(cancellationToken);

        var empresaIdsAMostrar = empresaIdsConRelacionVigente
            .Union(solicitudes.Select(s => s.EmpresaId))
            .ToList();

        // null = sin restricción (mismo contrato que el resto de *VisiblesAsync,
        // ver AlcanceDatosServiceExtensions); con restricción, se aplica a las
        // DOS mitades de la unión — no solo a la del historial — porque una
        // Empresa nunca debe aparecer por una vía que AlcanceDatosService no
        // conoce, ni siquiera la que ya pasaba por RelacionesEmpresariales.
        var empresaIdsVisibles = await alcanceDatos.ObtenerEmpresaIdsVisiblesAsync(cancellationToken);
        if (empresaIdsVisibles is not null)
            empresaIdsAMostrar = [.. empresaIdsAMostrar.Where(empresaIdsVisibles.Contains)];

        if (empresaIdsAMostrar.Count == 0)
            return [];

        var empresas = await empresasContext.Empresas
            .Where(e => empresaIdsAMostrar.Contains(e.Id))
            .OrderBy(e => e.RazonSocial)
            .Select(e => new { e.Id, e.RazonSocial, e.Cif })
            .ToListAsync(cancellationToken);

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var solicitudesPorEmpresa = solicitudes.GroupBy(s => s.EmpresaId).ToDictionary(g => g.Key, g => g.ToList());

        return empresas.Select(empresa =>
        {
            if (!solicitudesPorEmpresa.TryGetValue(empresa.Id, out var solicitudesEmpresa))
                return new BlindajeEmpresaDto(empresa.Id, empresa.RazonSocial, empresa.Cif, 0, null);

            var ultima = solicitudesEmpresa
                .OrderByDescending(s => s.FechaSolicitud)
                .ThenByDescending(s => s.CreadoEnUtc)
                .First();

            var estado = CalculadoraEstadoBlindaje42.Calcular(ultima.Resultado, ultima.FechaSolicitud, hoy);

            var ultimaDto = new UltimaSolicitudCertificacionTgssDto(
                ultima.Id, ultima.FechaSolicitud, ultima.FechaSolicitud.AddDays(SolicitudCertificacionTgss.PlazoDiasTgss),
                ultima.Resultado, ultima.FechaRespuesta, estado);

            return new BlindajeEmpresaDto(empresa.Id, empresa.RazonSocial, empresa.Cif, solicitudesEmpresa.Count, ultimaDto);
        }).ToList();
    }
}

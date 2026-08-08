using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Alertas.Queries.ObtenerSugerenciasPreventivas;

/// <summary>
/// Acción preventiva, no reactiva (pedido explícito del usuario, 2026-08-08):
/// cuando un Centro exige un TipoDocumento (vía TipoDocumentoCentro, ver
/// ResolucionTipoDocumentoCentro) y ya se detecta que a un Trabajador
/// asignado le falta, ¿a cuántos OTROS Trabajadores con Asignación activa al
/// mismo Centro les falta también? Agrupa exactamente los mismos
/// DocumentoFaltanteDto que ya calcula IDocumentosFaltantesService (Alertas,
/// P1-15 de MATURITY_REVIEW.md) — no es un concepto de dominio nuevo, es una
/// vista distinta sobre el mismo hueco: en vez de N alertas sueltas de
/// "Trabajador X, le falta Y", un patrón agrupado por Centro+TipoDocumento
/// que vale la pena resolver de una vez, antes de que aparezca el siguiente
/// técnico sin el documento.
///
/// Solo agrupa a partir de <see cref="UmbralMinimoTecnicosAfectados"/>: con
/// un único afectado la alerta individual de Alertas ya lo cubre — el valor
/// de esta vista es detectar el patrón, no duplicar cada alerta suelta.
/// </summary>
public record ObtenerSugerenciasPreventivasQuery : IRequest<IReadOnlyList<SugerenciaPreventivaDto>>;

public record SugerenciaPreventivaDto(
    Guid CentroId,
    string CentroNombre,
    Guid TipoDocumentoId,
    string TipoDocumentoNombre,
    IReadOnlyList<TrabajadorAfectadoDto> Trabajadores);

public record TrabajadorAfectadoDto(Guid TrabajadorId, string TrabajadorNombre);

public class ObtenerSugerenciasPreventivasQueryHandler(
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    IDocumentosFaltantesService documentosFaltantesService)
    : IRequestHandler<ObtenerSugerenciasPreventivasQuery, IReadOnlyList<SugerenciaPreventivaDto>>
{
    private const int UmbralMinimoTecnicosAfectados = 2;

    public async Task<IReadOnlyList<SugerenciaPreventivaDto>> Handle(
        ObtenerSugerenciasPreventivasQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var centroIdsVisibles = await alcanceDatos.ObtenerCentroIdsVisiblesAsync(cancellationToken);

        var asignacionesActivas = await (
            from asignacion in asignacionesContext.Asignaciones
            where asignacion.FechaBaja == null
            where centroIdsVisibles == null || centroIdsVisibles.Contains(asignacion.CentroId)
            where trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(asignacion.TrabajadorId)
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            select new
            {
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                CentroId = centro.Id,
                centro.Nombre
            })
            .Distinct()
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0) return [];

        var parejas = asignacionesActivas
            .Select(a => new ParejaTrabajadorCentro(a.TrabajadorId, a.TrabajadorNombre, a.CentroId, a.Nombre))
            .ToList();

        var faltantes = await documentosFaltantesService.CalcularAsync(parejas, cancellationToken);

        return faltantes
            .GroupBy(f => new { f.CentroId, f.CentroNombre, f.TipoDocumentoId, f.TipoDocumentoNombre })
            .Where(g => g.Count() >= UmbralMinimoTecnicosAfectados)
            .Select(g => new SugerenciaPreventivaDto(
                g.Key.CentroId, g.Key.CentroNombre, g.Key.TipoDocumentoId, g.Key.TipoDocumentoNombre,
                g.Select(f => new TrabajadorAfectadoDto(f.TrabajadorId, f.TrabajadorNombre))
                    .OrderBy(t => t.TrabajadorNombre)
                    .ToList()))
            .OrderByDescending(s => s.Trabajadores.Count)
            .ThenBy(s => s.CentroNombre)
            .ToList();
    }
}

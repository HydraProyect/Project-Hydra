using CaeManager.Application.Asignaciones;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas;

/// <summary>
/// Una incidencia documental de un Trabajador de la Subcontrata — a
/// diferencia de <c>CausaEstadoCentro</c> no lleva ámbito: Documento no tiene
/// propietario Subcontrata (ver DOMAIN.md, propietario polimórfico
/// excluyente), así que toda causa aquí es siempre de un Trabajador.
/// </summary>
public record IncidenciaSubcontrataDto(
    string Descripcion, EstadoDocumento Estado, Guid? DocumentoId, Guid? TipoDocumentoId, DateOnly? FechaVencimiento);

/// <summary>Desglose de las incidencias de una Subcontrata por estado — mismo criterio que <c>RecuentosCentroDto</c>: no lleva contadores propios, se derivan de las listas.</summary>
public record RecuentosSubcontrataDto(
    IReadOnlyList<IncidenciaSubcontrataDto> Vencidas,
    IReadOnlyList<IncidenciaSubcontrataDto> Proximas)
{
    public static readonly RecuentosSubcontrataDto Vacio = new([], []);

    public int TotalVencidas => Vencidas.Count;
    public int TotalProximas => Proximas.Count;
}

/// <summary>
/// Cálculo de cumplimiento documental de una Subcontrata ("Subcontrata 360",
/// mismo patrón que <see cref="ICalculoEstadoCentroService"/> para Centro
/// 360): agrega los Documentos de los Trabajadores de la Subcontrata frente a
/// lo que exige CADA Centro donde tienen una Asignación activa — un
/// Trabajador con más de un Centro activo cuenta un TipoDocumento como
/// exigido si lo exige cualquiera de ellos (el Documento es del Trabajador,
/// no del Centro, así que un único hecho de cumplimiento vale para todos).
/// Sin Documentos de "Empresa" ni <c>BloqueaAcceso</c>: esos conceptos son de
/// Centro y no tienen equivalente a nivel de Subcontrata.
/// </summary>
public interface ICalculoEstadoSubcontrataService
{
    Task<IReadOnlyDictionary<Guid, IReadOnlyList<IncidenciaSubcontrataDto>>> CalcularAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken);
}

public class CalculoEstadoSubcontrataService(
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos)
    : ICalculoEstadoSubcontrataService
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IncidenciaSubcontrataDto>>> CalcularAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
    {
        var (_, causasPorSubcontrata) = await CalcularBaseAsync(subcontrataIds, cancellationToken);
        return causasPorSubcontrata.ToDictionary(p => p.Key, p => (IReadOnlyList<IncidenciaSubcontrataDto>)p.Value);
    }

    public async Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
    {
        var (fraccionPorSubcontrata, _) = await CalcularBaseAsync(subcontrataIds, cancellationToken);
        return fraccionPorSubcontrata.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));
    }

    private async Task<(
        Dictionary<Guid, (int AlDia, int Requeridos)> Fraccion,
        Dictionary<Guid, List<IncidenciaSubcontrataDto>> Causas)> CalcularBaseAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
    {
        var fraccionPorSubcontrata = subcontrataIds.Distinct().ToDictionary(id => id, _ => (AlDia: 0, Requeridos: 0));
        var causasPorSubcontrata = subcontrataIds.Distinct().ToDictionary(id => id, _ => new List<IncidenciaSubcontrataDto>());

        if (subcontrataIds.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata);

        var consultaTrabajadores = trabajadoresContext.Trabajadores
            .Where(t => t.SubcontrataId != null && subcontrataIds.Contains(t.SubcontrataId!.Value));

        // Mismo criterio que ObtenerTrabajadoresQuery: la Subcontrata visible
        // no implica que todos sus Trabajadores lo sean para la cartera del
        // usuario actual.
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadorIdsVisibles is not null)
            consultaTrabajadores = consultaTrabajadores.Where(t => trabajadorIdsVisibles.Contains(t.Id));

        var trabajadores = await consultaTrabajadores
            .Select(t => new { t.Id, SubcontrataId = t.SubcontrataId!.Value, Nombre = t.Nombre + " " + t.Apellidos })
            .ToListAsync(cancellationToken);

        if (trabajadores.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata);

        var subcontrataPorTrabajador = trabajadores.ToDictionary(t => t.Id, t => t.SubcontrataId);
        var nombrePorTrabajador = trabajadores.ToDictionary(t => t.Id, t => t.Nombre);
        var trabajadorIds = trabajadores.Select(t => t.Id).ToList();

        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && trabajadorIds.Contains(a.TrabajadorId))
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata);

        var centrosPorTrabajador = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.CentroId).Distinct().ToList());

        var centroIds = asignacionesActivas.Select(a => a.CentroId).Distinct().ToList();

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata);

        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();
        var nombrePorTipo = tiposCandidatos.ToDictionary(t => t.Id, t => t.Nombre);

        var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        // Unión por trabajador: un tipo cuenta como exigido si lo exige
        // cualquiera de sus centros activos — el Documento es suyo, no del
        // Centro, así que un solo hecho de cumplimiento cubre a todos.
        var tiposRequeridosPorTrabajador = new Dictionary<Guid, List<Guid>>();
        foreach (var trabajadorId in trabajadorIds)
        {
            var centrosDelTrabajador = centrosPorTrabajador.GetValueOrDefault(trabajadorId, []);
            if (centrosDelTrabajador.Count == 0)
            {
                tiposRequeridosPorTrabajador[trabajadorId] = [];
                continue;
            }

            tiposRequeridosPorTrabajador[trabajadorId] = tiposCandidatos
                .Where(t => centrosDelTrabajador.Any(centroId => ResolucionTipoDocumentoCentro.Aplica(filasPorPar, t.Id, centroId, t.EsObligatorio)))
                .Select(t => t.Id)
                .ToList();
        }

        var tipoIdsRequeridosGlobal = tiposRequeridosPorTrabajador.Values.SelectMany(t => t).Distinct().ToList();
        if (tipoIdsRequeridosGlobal.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata);

        var documentosExistentes = await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsRequeridosGlobal.Contains(d.TipoDocumentoId))
            .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        var documentosPorPar = documentosExistentes.ToDictionary(d => (d.TrabajadorId, d.TipoDocumentoId));

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        foreach (var trabajadorId in trabajadorIds)
        {
            var subcontrataId = subcontrataPorTrabajador[trabajadorId];
            var actual = fraccionPorSubcontrata[subcontrataId];

            foreach (var tipoId in tiposRequeridosPorTrabajador[trabajadorId])
            {
                var tieneDocumento = documentosPorPar.TryGetValue((trabajadorId, tipoId), out var documento);
                var estado = tieneDocumento
                    ? CalculadoraEstadoDocumento.Calcular(documento!.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                    : EstadoDocumento.Faltante;

                var alDia = actual.AlDia + (estado is EstadoDocumento.Vigente or EstadoDocumento.NoAplica ? 1 : 0);
                actual = (alDia, actual.Requeridos + 1);

                if (estado is EstadoDocumento.NoAplica or EstadoDocumento.Vigente) continue;

                causasPorSubcontrata[subcontrataId].Add(new IncidenciaSubcontrataDto(
                    $"{nombrePorTipo[tipoId]} — {nombrePorTrabajador[trabajadorId]}", estado,
                    tieneDocumento ? documento!.Id : null, tipoId, tieneDocumento ? documento!.FechaVencimiento : null));
            }

            fraccionPorSubcontrata[subcontrataId] = actual;
        }

        return (fraccionPorSubcontrata, causasPorSubcontrata);
    }
}

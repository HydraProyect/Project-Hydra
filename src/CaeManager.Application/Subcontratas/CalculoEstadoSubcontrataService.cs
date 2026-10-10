using CaeManager.Domain.Common;
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
/// propietario Subcontrata (ver Project-Hydra-Negocio/tecnico/DOMAIN.md, propietario polimórfico
/// excluyente), así que toda causa aquí es siempre de un Trabajador.
/// </summary>
public record IncidenciaSubcontrataDto(
    string Descripcion, EstadoDocumento Estado, Guid? DocumentoId, Guid? TipoDocumentoId, DateOnly? FechaVencimiento,
    Guid? TrabajadorId = null);

/// <summary>Desglose de las incidencias de una Subcontrata por estado — mismo criterio que <c>RecuentosCentroDto</c>: no lleva contadores propios, se derivan de las listas.</summary>
public record RecuentosSubcontrataDto(
    IReadOnlyList<IncidenciaSubcontrataDto> Vencidas,
    IReadOnlyList<IncidenciaSubcontrataDto> Proximas)
{
    public static readonly RecuentosSubcontrataDto Vacio = new([], []);

    /// <summary>
    /// Documentos exigidos cuya vigencia nadie ha confirmado. No son incidencia de color (no tiñen la fila
    /// ni entran en <see cref="Vencidas"/> o <see cref="Proximas"/>), pero deciden el estado documental de la
    /// Subcontrata cuando no hay nada peor y se dicen en el motivo del listado.
    /// </summary>
    public IReadOnlyList<IncidenciaSubcontrataDto> SinConfirmar { get; init; } = [];

    public int TotalVencidas => Vencidas.Count;
    public int TotalProximas => Proximas.Count;
    public int TotalSinConfirmar => SinConfirmar.Count;

    /// <summary>
    /// Estado documental de la Subcontrata: el peor de sus incidencias. «Faltante» viaja en
    /// <see cref="Vencidas"/> y se lee como Vencido (mismo criterio que el desglose del listado). Sin nada
    /// pendiente es <see cref="EstadoDocumento.Vigente"/>, también cuando ningún Centro exige nada.
    /// </summary>
    public EstadoDocumento PeorEstado =>
        TotalVencidas > 0 ? EstadoDocumento.Vencido
        : Proximas.Any(p => p.Estado == EstadoDocumento.Urgente) ? EstadoDocumento.Urgente
        : TotalProximas > 0 ? EstadoDocumento.Proximo
        : TotalSinConfirmar > 0 ? EstadoDocumento.SinConfirmar
        : EstadoDocumento.Vigente;
}

/// <summary>Lo que el listado necesita de una Subcontrata, calculado en una sola pasada.</summary>
public record ResumenEstadoSubcontrata(
    FraccionCumplimiento Fraccion,
    IReadOnlyList<IncidenciaSubcontrataDto> Incidencias,
    IReadOnlyList<IncidenciaSubcontrataDto> SinConfirmar);

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

    /// <summary>
    /// Cumplimiento, incidencias y documentos sin confirmar de cada Subcontrata, con el mismo alcance y las
    /// mismas reglas que los otros dos métodos, en una sola pasada por los datos.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, ResumenEstadoSubcontrata>> CalcularResumenAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken);
}

public class CalculoEstadoSubcontrataService(
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IConfiguracionQueryContext configuracionContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos)
    : ICalculoEstadoSubcontrataService
{
    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<IncidenciaSubcontrataDto>>> CalcularAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
    {
        var (_, causasPorSubcontrata, _) = await CalcularBaseAsync(subcontrataIds, cancellationToken);
        return causasPorSubcontrata.ToDictionary(p => p.Key, p => (IReadOnlyList<IncidenciaSubcontrataDto>)p.Value);
    }

    public async Task<IReadOnlyDictionary<Guid, FraccionCumplimiento>> CalcularCumplimientoAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
    {
        var (fraccionPorSubcontrata, _, _) = await CalcularBaseAsync(subcontrataIds, cancellationToken);
        return fraccionPorSubcontrata.ToDictionary(p => p.Key, p => new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos));
    }

    public async Task<IReadOnlyDictionary<Guid, ResumenEstadoSubcontrata>> CalcularResumenAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
    {
        var (fraccionPorSubcontrata, causasPorSubcontrata, sinConfirmarPorSubcontrata) = await CalcularBaseAsync(subcontrataIds, cancellationToken);
        return fraccionPorSubcontrata.ToDictionary(
            p => p.Key,
            p => new ResumenEstadoSubcontrata(
                new FraccionCumplimiento(p.Value.AlDia, p.Value.Requeridos),
                causasPorSubcontrata[p.Key],
                sinConfirmarPorSubcontrata[p.Key]));
    }

    private async Task<(
        Dictionary<Guid, (int AlDia, int Requeridos)> Fraccion,
        Dictionary<Guid, List<IncidenciaSubcontrataDto>> Causas,
        Dictionary<Guid, List<IncidenciaSubcontrataDto>> SinConfirmar)> CalcularBaseAsync(
        IReadOnlyList<Guid> subcontrataIds, CancellationToken cancellationToken)
    {
        var fraccionPorSubcontrata = subcontrataIds.Distinct().ToDictionary(id => id, _ => (AlDia: 0, Requeridos: 0));
        var causasPorSubcontrata = subcontrataIds.Distinct().ToDictionary(id => id, _ => new List<IncidenciaSubcontrataDto>());
        var sinConfirmarPorSubcontrata = subcontrataIds.Distinct().ToDictionary(id => id, _ => new List<IncidenciaSubcontrataDto>());

        if (subcontrataIds.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata, sinConfirmarPorSubcontrata);

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
            return (fraccionPorSubcontrata, causasPorSubcontrata, sinConfirmarPorSubcontrata);

        var subcontrataPorTrabajador = trabajadores.ToDictionary(t => t.Id, t => t.SubcontrataId);
        var nombrePorTrabajador = trabajadores.ToDictionary(t => t.Id, t => t.Nombre);
        var trabajadorIds = trabajadores.Select(t => t.Id).ToList();

        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && trabajadorIds.Contains(a.TrabajadorId))
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);

        if (asignacionesActivas.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata, sinConfirmarPorSubcontrata);

        var centrosPorTrabajador = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.CentroId).Distinct().ToList());

        // P1-X2: un Centro sin gestión CAE no exige ningún tipo a quien trabaja allí.
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(
            centrosContext, asignacionesActivas.Select(a => a.CentroId), cancellationToken);

        var centroIds = asignacionesActivas.Select(a => a.CentroId).Distinct().ToList();

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);

        if (tiposCandidatos.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata, sinConfirmarPorSubcontrata);

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
                .Where(t => centrosDelTrabajador.Any(centroId => !sinGestionCae.Contains(centroId) && ResolucionTipoDocumentoCentro.Aplica(filasPorPar, t.Id, centroId, t.CuentaParaCumplimiento)))
                .Select(t => t.Id)
                .ToList();
        }

        var tipoIdsRequeridosGlobal = tiposRequeridosPorTrabajador.Values.SelectMany(t => t).Distinct().ToList();
        if (tipoIdsRequeridosGlobal.Count == 0)
            return (fraccionPorSubcontrata, causasPorSubcontrata, sinConfirmarPorSubcontrata);

        var documentosExistentes = await documentosContext.Documentos.Operativos()
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsRequeridosGlobal.Contains(d.TipoDocumentoId))
            .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
            .ToListAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DiaDeNegocio.Hoy();

        // Un documento operativo por par en el caso normal; con duplicados sin resolver manda el efectivo.
        var documentosPorPar = DocumentoEfectivo.UnoPorClave(
            documentosExistentes, d => (d.TrabajadorId, d.TipoDocumentoId), d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy);

        foreach (var trabajadorId in trabajadorIds)
        {
            var subcontrataId = subcontrataPorTrabajador[trabajadorId];
            var actual = fraccionPorSubcontrata[subcontrataId];

            foreach (var tipoId in tiposRequeridosPorTrabajador[trabajadorId])
            {
                var tieneDocumento = documentosPorPar.TryGetValue((trabajadorId, tipoId), out var documento);
                var estado = tieneDocumento
                    ? CalculadoraEstadoDocumento.Calcular(documento!.EstadoVigencia, documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                    : EstadoDocumento.Faltante;

                var alDia = actual.AlDia + (CumplimientoDocumental.EsConforme(estado) ? 1 : 0);
                actual = (alDia, actual.Requeridos + 1);

                // Sin vigencia confirmada no cuenta como al día en el porcentaje (no suma arriba) pero
                // tampoco es incidencia de color: mismo criterio que el Centro
                // (CalculoEstadoCentroService.AgregarCausasDeEmpresaAsync). Próximo y Urgente suman al día
                // y siguen siendo incidencia: son dos preguntas distintas.
                // Lo sin confirmar se apunta aparte: decide el estado documental del listado cuando no hay
                // nada peor, sin pasar a ser incidencia para quien lee solo las causas.
                if (estado is EstadoDocumento.SinCaducidad or EstadoDocumento.Vigente) continue;

                var incidencia = new IncidenciaSubcontrataDto(
                    $"{nombrePorTipo[tipoId]} — {nombrePorTrabajador[trabajadorId]}", estado,
                    tieneDocumento ? documento!.Id : null, tipoId, tieneDocumento ? documento!.FechaVencimiento : null, trabajadorId);
                (estado is EstadoDocumento.SinConfirmar ? sinConfirmarPorSubcontrata : causasPorSubcontrata)[subcontrataId].Add(incidencia);
            }

            fraccionPorSubcontrata[subcontrataId] = actual;
        }

        return (fraccionPorSubcontrata, causasPorSubcontrata, sinConfirmarPorSubcontrata);
    }
}

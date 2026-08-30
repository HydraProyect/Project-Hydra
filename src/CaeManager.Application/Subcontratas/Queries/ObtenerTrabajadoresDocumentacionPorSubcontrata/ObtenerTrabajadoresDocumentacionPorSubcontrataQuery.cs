using CaeManager.Application.Asignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Subcontratas.Queries.ObtenerTrabajadoresDocumentacionPorSubcontrata;

/// <summary>
/// Alimenta el segundo y tercer nivel de "Subcontrata 360" (mismo patrón que
/// <c>ObtenerAsignacionesDocumentacionPorCentroQuery</c> para Centro 360):
/// los Trabajadores de la Subcontrata con el estado de la documentación que
/// exige CUALQUIERA de los Centros donde tienen una Asignación activa — un
/// Trabajador con varios Centros activos ve la unión de lo que le exige cada
/// uno, porque el Documento es suyo, no del Centro (mismo criterio que
/// <see cref="ICalculoEstadoSubcontrataService"/>, que agrega el mismo cálculo
/// a nivel de Subcontrata para la fila de la lista). Se calcula de una sola
/// vez al expandir la fila — el tercer nivel no vuelve a consultar el
/// servidor.
///
/// Clave por <b>Trabajador</b>, no por Asignación (a diferencia de Centro
/// 360): un Trabajador de Subcontrata puede estar activo en más de un Centro
/// a la vez, y aquí interesa una sola fila por persona, no una por cada sitio
/// donde trabaja.
/// </summary>
public record ObtenerTrabajadoresDocumentacionPorSubcontrataQuery(Guid SubcontrataId)
    : IRequest<IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>>;

public record TrabajadorDocumentacionSubcontrataDto(
    Guid TrabajadorId, string TrabajadorNombre, string? TrabajadorDni,
    EstadoDocumento PeorEstado, IReadOnlyList<DocumentoRequeridoDto> Documentos);

public class ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler(
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerTrabajadoresDocumentacionPorSubcontrataQuery, IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>>
{
    private static readonly IReadOnlyDictionary<EstadoDocumento, int> OrdenSeveridad = new Dictionary<EstadoDocumento, int>
    {
        [EstadoDocumento.Faltante] = 0,
        [EstadoDocumento.Vencido] = 1,
        [EstadoDocumento.Urgente] = 2,
        [EstadoDocumento.Proximo] = 3,
        [EstadoDocumento.Vigente] = 4
    };

    public async Task<IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>> Handle(
        ObtenerTrabajadoresDocumentacionPorSubcontrataQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.SubcontrataVisibleAsync(request.SubcontrataId, cancellationToken))
            return [];

        var consultaTrabajadores = trabajadoresContext.Trabajadores
            .Where(t => t.SubcontrataId == request.SubcontrataId);

        // La Subcontrata visible no implica que todos sus Trabajadores lo
        // sean: el alcance por cartera (IAlcanceDatosService) es la frontera
        // real, mismo criterio que ObtenerTrabajadoresQuery — sin esto, un
        // Gestor CAE con cartera acotada vería aquí trabajadores fuera de su
        // cartera que ObtenerTrabajadorPorIdQuery rechazaría al abrir.
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        if (trabajadorIdsVisibles is not null)
            consultaTrabajadores = consultaTrabajadores.Where(t => trabajadorIdsVisibles.Contains(t.Id));

        var trabajadores = await consultaTrabajadores
            .OrderBy(t => t.Apellidos).ThenBy(t => t.Nombre)
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos, t.Dni })
            .ToListAsync(cancellationToken);

        if (trabajadores.Count == 0)
            return [];

        var trabajadorIds = trabajadores.Select(t => t.Id).ToList();

        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && trabajadorIds.Contains(a.TrabajadorId))
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);

        var centrosPorTrabajador = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.CentroId).Distinct().ToList());

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si })
            .ToListAsync(cancellationToken);

        var tiposRequeridosPorTrabajador = new Dictionary<Guid, List<Guid>>();

        if (tiposCandidatos.Count > 0 && asignacionesActivas.Count > 0)
        {
            var centroIds = asignacionesActivas.Select(a => a.CentroId).Distinct().ToList();
            var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

            var filasPorPar = (await tiposDocumentoContext.TiposDocumentoCentros
                .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
                .ToListAsync(cancellationToken))
                .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

            foreach (var trabajadorId in trabajadorIds)
            {
                var centrosDelTrabajador = centrosPorTrabajador.GetValueOrDefault(trabajadorId, []);
                tiposRequeridosPorTrabajador[trabajadorId] = centrosDelTrabajador.Count == 0
                    ? []
                    : tiposCandidatos
                        .Where(t => centrosDelTrabajador.Any(centroId => ResolucionTipoDocumentoCentro.Aplica(filasPorPar, t.Id, centroId, t.CuentaParaCumplimiento)))
                        .Select(t => t.Id)
                        .ToList();
            }
        }
        else
        {
            foreach (var trabajadorId in trabajadorIds)
                tiposRequeridosPorTrabajador[trabajadorId] = [];
        }

        var nombrePorTipo = tiposCandidatos.ToDictionary(t => t.Id, t => t.Nombre);
        var tipoIdsRequeridosGlobal = tiposRequeridosPorTrabajador.Values.SelectMany(t => t).Distinct().ToList();

        var documentosPorTrabajador = tipoIdsRequeridosGlobal.Count == 0
            ? new Dictionary<Guid, List<(Guid Id, Guid TipoDocumentoId, DateOnly? FechaVencimiento)>>()
            : (await documentosContext.Documentos
                .Where(d => d.TrabajadorId != null
                    && trabajadorIds.Contains(d.TrabajadorId!.Value)
                    && tipoIdsRequeridosGlobal.Contains(d.TipoDocumentoId))
                .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
                .ToListAsync(cancellationToken))
                .GroupBy(d => d.TrabajadorId)
                .ToDictionary(g => g.Key, g => g.Select(d => (d.Id, d.TipoDocumentoId, d.FechaVencimiento)).ToList());

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var resultado = new List<TrabajadorDocumentacionSubcontrataDto>();

        foreach (var trabajador in trabajadores)
        {
            var documentosDelTrabajador = documentosPorTrabajador.GetValueOrDefault(trabajador.Id, []);
            var tipoIdsConDocumento = documentosDelTrabajador.Select(d => d.TipoDocumentoId).ToHashSet();

            var items = new List<DocumentoRequeridoDto>();

            foreach (var documento in documentosDelTrabajador)
            {
                var estado = CalculadoraEstadoDocumento.Calcular(
                    documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                if (estado == EstadoDocumento.SinCaducidad) continue;

                items.Add(new DocumentoRequeridoDto(
                    documento.Id, documento.TipoDocumentoId, nombrePorTipo[documento.TipoDocumentoId], estado, documento.FechaVencimiento));
            }

            foreach (var tipoId in tiposRequeridosPorTrabajador[trabajador.Id])
            {
                if (tipoIdsConDocumento.Contains(tipoId)) continue;
                items.Add(new DocumentoRequeridoDto(null, tipoId, nombrePorTipo[tipoId], EstadoDocumento.Faltante, null));
            }

            var ordenados = items.OrderBy(i => OrdenSeveridad[i.Estado]).ThenBy(i => i.TipoDocumentoNombre).ToList();
            var peorEstado = ordenados.Count > 0 ? ordenados[0].Estado : EstadoDocumento.Vigente;

            resultado.Add(new TrabajadorDocumentacionSubcontrataDto(
                trabajador.Id, trabajador.Nombre, trabajador.Dni, peorEstado, ordenados));
        }

        return resultado;
    }
}

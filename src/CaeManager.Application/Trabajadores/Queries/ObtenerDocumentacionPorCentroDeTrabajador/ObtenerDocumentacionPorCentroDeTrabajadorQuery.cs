using CaeManager.Application.Asignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Trabajadores.Queries.ObtenerDocumentacionPorCentroDeTrabajador;

/// <summary>
/// Alimenta el ámbito "Documentación por centro" de Trabajador 360 (Parte
/// XVI PROMPT 04) — la inversa de <see cref="ObtenerAsignacionesDocumentacionPorCentroQuery"/>:
/// esa va indexada por Centro (todos sus trabajadores), esta va indexada por
/// Trabajador (todos sus centros). Reutiliza la misma semántica de
/// resolución (<see cref="ResolucionTipoDocumentoCentro"/>,
/// <see cref="CalculadoraEstadoDocumento"/>, mismo <see cref="DocumentoRequeridoDto"/>)
/// en vez de reimplementarla — cada centro puede exigir un catálogo
/// distinto, así que el cálculo se hace centro a centro, pero sobre un
/// único trabajador ya cargado una vez.
/// </summary>
public record ObtenerDocumentacionPorCentroDeTrabajadorQuery(Guid TrabajadorId)
    : IRequest<IReadOnlyList<CentroDocumentacionTrabajadorDto>>;

public record CentroDocumentacionTrabajadorDto(
    Guid AsignacionId, Guid CentroId, string CentroNombre, Guid ClienteId, string ClienteRazonSocial,
    DateOnly FechaAlta, EstadoDocumento PeorEstado, IReadOnlyList<DocumentoRequeridoDto> Documentos);

public class ObtenerDocumentacionPorCentroDeTrabajadorQueryHandler(
    IAsignacionesQueryContext asignacionesContext,
    ICentrosQueryContext centrosContext,
    IEmpresasQueryContext empresasContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionPorCentroDeTrabajadorQuery, IReadOnlyList<CentroDocumentacionTrabajadorDto>>
{
    private static readonly IReadOnlyDictionary<EstadoDocumento, int> OrdenSeveridad = new Dictionary<EstadoDocumento, int>
    {
        [EstadoDocumento.Faltante] = 0,
        [EstadoDocumento.Vencido] = 1,
        [EstadoDocumento.Urgente] = 2,
        [EstadoDocumento.Proximo] = 3,
        [EstadoDocumento.Vigente] = 4
    };

    public async Task<IReadOnlyList<CentroDocumentacionTrabajadorDto>> Handle(
        ObtenerDocumentacionPorCentroDeTrabajadorQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.TrabajadorVisibleAsync(request.TrabajadorId, cancellationToken))
            return [];

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            join centro in centrosContext.Centros on asignacion.CentroId equals centro.Id
            join cliente in empresasContext.Empresas on centro.ClienteId equals cliente.Id
            where asignacion.TrabajadorId == request.TrabajadorId && asignacion.FechaBaja == null
            orderby centro.Nombre
            select new
            {
                AsignacionId = asignacion.Id,
                CentroId = centro.Id,
                CentroNombre = centro.Nombre,
                ClienteId = cliente.Id,
                ClienteRazonSocial = cliente.RazonSocial,
                asignacion.FechaAlta
            })
            .ToListAsync(cancellationToken);

        if (asignaciones.Count == 0)
            return [];

        var centroIds = asignaciones.Select(a => a.CentroId).ToList();

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, t.EsObligatorio })
            .ToListAsync(cancellationToken);
        var tipoIdsCandidatos = tiposCandidatos.Select(t => t.Id).ToHashSet();

        var filasDeLosCentros = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsCandidatos.Contains(tc.TipoDocumentoId) && centroIds.Contains(tc.CentroId))
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        var documentosDelTrabajador = await documentosContext.Documentos
            .Where(d => d.TrabajadorId == request.TrabajadorId)
            .Select(d => new { d.Id, d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken);
        var documentosPorTipo = documentosDelTrabajador.ToDictionary(d => d.TipoDocumentoId);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var resultado = new List<CentroDocumentacionTrabajadorDto>();

        foreach (var asignacion in asignaciones)
        {
            var tiposRequeridos = tiposCandidatos
                .Where(t => ResolucionTipoDocumentoCentro.Aplica(filasDeLosCentros, t.Id, asignacion.CentroId, t.EsObligatorio))
                .ToList();

            var items = new List<DocumentoRequeridoDto>();

            foreach (var tipo in tiposRequeridos)
            {
                if (!documentosPorTipo.TryGetValue(tipo.Id, out var documento))
                {
                    items.Add(new DocumentoRequeridoDto(null, tipo.Id, tipo.Nombre, EstadoDocumento.Faltante, null));
                    continue;
                }

                var estado = CalculadoraEstadoDocumento.Calcular(
                    documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                if (estado == EstadoDocumento.NoAplica) continue;

                items.Add(new DocumentoRequeridoDto(documento.Id, tipo.Id, tipo.Nombre, estado, documento.FechaVencimiento));
            }

            var ordenados = items.OrderBy(i => OrdenSeveridad[i.Estado]).ThenBy(i => i.TipoDocumentoNombre).ToList();
            var peorEstado = ordenados.Count > 0 ? ordenados[0].Estado : EstadoDocumento.Vigente;

            resultado.Add(new CentroDocumentacionTrabajadorDto(
                asignacion.AsignacionId, asignacion.CentroId, asignacion.CentroNombre, asignacion.ClienteId, asignacion.ClienteRazonSocial,
                asignacion.FechaAlta, peorEstado, ordenados));
        }

        return resultado;
    }
}

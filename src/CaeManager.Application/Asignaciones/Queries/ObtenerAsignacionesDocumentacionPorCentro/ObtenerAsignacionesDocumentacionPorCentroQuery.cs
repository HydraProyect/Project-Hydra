using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;

/// <summary>
/// Alimenta el acordeón de asignaciones de Centro 360 (<c>PLAN-EJECUCION-UX.md</c>
/// § 0.1/0.2): trabajadores con Asignación activa en un Centro, con el estado
/// de la documentación que ESE centro exige de cada uno — misma allow-list de
/// <c>TipoDocumentoCentro</c> que <see cref="Alertas.IDocumentosFaltantesService"/>
/// y <c>ObtenerDocumentacionVisitaQuery</c> (un tipo sin ninguna fila ahí
/// aplica a todos los centros; con filas, se restringe a esos). Se calcula de
/// una sola vez para todos los trabajadores del centro al expandir la fila —
/// el tercer nivel (documentos de un trabajador concreto) no vuelve a
/// consultar el servidor, ya tiene el desglose completo en memoria.
/// </summary>
/// <param name="VentanaVisitaFechaFin">
/// <c>FechaFin</c> de la próxima visita activa del centro, si tiene una
/// (§ 0.3) — permite marcar <see cref="DocumentoRequeridoDto.CaducaEnVentanaVisita"/>
/// sin recalcular el estado del documento: sigue siendo <c>Vigente</c> con
/// fecha de referencia = hoy, solo se compara además contra esta fecha.
/// </param>
public record ObtenerAsignacionesDocumentacionPorCentroQuery(Guid CentroId, DateOnly? VentanaVisitaFechaFin = null)
    : IRequest<IReadOnlyList<TrabajadorAsignacionDocumentacionDto>>;

/// <param name="CaducaEnVentanaVisita">
/// Solo relevante cuando <see cref="EstadoDocumento.Vigente"/>: el documento
/// caduca antes de que termine la próxima visita del centro (comparando
/// <see cref="CalculadoraEstadoDocumento"/> con fecha de referencia = fin de
/// la visita en vez de hoy) — modificador visual "vigente con riesgo en
/// ventana", no un estado nuevo (PLAN-EJECUCION-UX.md § 0.3).
/// </param>
public record DocumentoRequeridoDto(
    Guid? DocumentoId, Guid TipoDocumentoId, string TipoDocumentoNombre, EstadoDocumento Estado,
    DateOnly? FechaVencimiento, bool CaducaEnVentanaVisita = false);

public record TrabajadorAsignacionDocumentacionDto(
    Guid AsignacionId, Guid TrabajadorId, string TrabajadorNombre, DateOnly FechaAlta,
    EstadoDocumento PeorEstado, IReadOnlyList<DocumentoRequeridoDto> Documentos);

public class ObtenerAsignacionesDocumentacionPorCentroQueryHandler(
    IAsignacionesQueryContext asignacionesContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerAsignacionesDocumentacionPorCentroQuery, IReadOnlyList<TrabajadorAsignacionDocumentacionDto>>
{
    private static readonly IReadOnlyDictionary<EstadoDocumento, int> OrdenSeveridad = new Dictionary<EstadoDocumento, int>
    {
        [EstadoDocumento.Faltante] = 0,
        [EstadoDocumento.Vencido] = 1,
        [EstadoDocumento.Urgente] = 2,
        [EstadoDocumento.Proximo] = 3,
        [EstadoDocumento.Vigente] = 4
    };

    public async Task<IReadOnlyList<TrabajadorAsignacionDocumentacionDto>> Handle(
        ObtenerAsignacionesDocumentacionPorCentroQuery request, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.CentroVisibleAsync(request.CentroId, cancellationToken))
            return [];

        var asignaciones = await (
            from asignacion in asignacionesContext.Asignaciones
            join trabajador in trabajadoresContext.Trabajadores on asignacion.TrabajadorId equals trabajador.Id
            where asignacion.CentroId == request.CentroId && asignacion.FechaBaja == null
            orderby trabajador.Apellidos, trabajador.Nombre
            select new
            {
                AsignacionId = asignacion.Id,
                TrabajadorId = trabajador.Id,
                TrabajadorNombre = trabajador.Nombre + " " + trabajador.Apellidos,
                asignacion.FechaAlta
            })
            .ToListAsync(cancellationToken);

        if (asignaciones.Count == 0)
            return [];

        var tiposObligatorios = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && t.EsObligatorio)
            .Select(t => new { t.Id, t.Nombre })
            .ToListAsync(cancellationToken);

        // Tipos requeridos por ESTE centro: sin fila en TipoDocumentoCentro
        // aplica a todos; con filas, solo si este centro está entre ellas.
        var tipoIdsObligatorios = tiposObligatorios.Select(t => t.Id).ToHashSet();
        var restriccionesPorTipo = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsObligatorios.Contains(tc.TipoDocumentoId))
            .Select(tc => new { tc.TipoDocumentoId, tc.CentroId })
            .ToListAsync(cancellationToken))
            .GroupBy(tc => tc.TipoDocumentoId)
            .ToDictionary(g => g.Key, g => g.Select(tc => tc.CentroId).ToHashSet());

        var tiposRequeridosPorCentro = tiposObligatorios
            .Where(t => !restriccionesPorTipo.TryGetValue(t.Id, out var centros) || centros.Contains(request.CentroId))
            .ToList();

        if (tiposRequeridosPorCentro.Count == 0)
        {
            return asignaciones
                .Select(a => new TrabajadorAsignacionDocumentacionDto(
                    a.AsignacionId, a.TrabajadorId, a.TrabajadorNombre, a.FechaAlta, EstadoDocumento.Vigente, []))
                .ToList();
        }

        var tipoIdsRequeridos = tiposRequeridosPorCentro.Select(t => t.Id).ToHashSet();
        var trabajadorIds = asignaciones.Select(a => a.TrabajadorId).Distinct().ToList();

        var documentosExistentes = await documentosContext.Documentos
            .Where(d => d.TrabajadorId != null
                && trabajadorIds.Contains(d.TrabajadorId!.Value)
                && tipoIdsRequeridos.Contains(d.TipoDocumentoId))
            .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var documentosPorTrabajador = documentosExistentes
            .GroupBy(d => d.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var resultado = new List<TrabajadorAsignacionDocumentacionDto>();

        foreach (var asignacion in asignaciones)
        {
            var documentosDelTrabajador = documentosPorTrabajador.GetValueOrDefault(asignacion.TrabajadorId, []);
            var tipoIdsConDocumento = documentosDelTrabajador.Select(d => d.TipoDocumentoId).ToHashSet();

            var items = new List<DocumentoRequeridoDto>();

            foreach (var documento in documentosDelTrabajador)
            {
                var estado = CalculadoraEstadoDocumento.Calcular(
                    documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                if (estado == EstadoDocumento.NoAplica) continue;

                // Solo tiene sentido avisar de un documento que hoy está bien
                // pero no aguantará hasta el final de la visita — uno que ya
                // está en Próximo/Urgente/Vencido no necesita un modificador
                // aparte, ya se ve en su propio badge.
                var caducaEnVentana = estado == EstadoDocumento.Vigente
                    && request.VentanaVisitaFechaFin is { } finVisita
                    && CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, finVisita, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                        is not (EstadoDocumento.Vigente or EstadoDocumento.NoAplica);

                var nombreTipo = tiposRequeridosPorCentro.First(t => t.Id == documento.TipoDocumentoId).Nombre;
                items.Add(new DocumentoRequeridoDto(documento.Id, documento.TipoDocumentoId, nombreTipo, estado, documento.FechaVencimiento, caducaEnVentana));
            }

            foreach (var tipo in tiposRequeridosPorCentro)
            {
                if (tipoIdsConDocumento.Contains(tipo.Id)) continue;
                items.Add(new DocumentoRequeridoDto(null, tipo.Id, tipo.Nombre, EstadoDocumento.Faltante, null));
            }

            var ordenados = items.OrderBy(i => OrdenSeveridad[i.Estado]).ThenBy(i => i.TipoDocumentoNombre).ToList();
            var peorEstado = ordenados.Count > 0 ? ordenados[0].Estado : EstadoDocumento.Vigente;

            resultado.Add(new TrabajadorAsignacionDocumentacionDto(
                asignacion.AsignacionId, asignacion.TrabajadorId, asignacion.TrabajadorNombre, asignacion.FechaAlta,
                peorEstado, ordenados));
        }

        return resultado;
    }
}

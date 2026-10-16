using CaeManager.Domain.Common;
using CaeManager.Application.Centros;
using CaeManager.Application.Asignaciones;
using CaeManager.Application.Asignaciones.Queries.ObtenerAsignacionesDocumentacionPorCentro;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Proyectos;
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

/// <param name="Documentos">
/// Lo que se enseña al expandir al Trabajador: no lista «Sin caducidad» y, si hay un documento vencido y su
/// renovación del mismo tipo, enseña los dos. NO sirve para contar el cumplimiento: para eso está
/// <paramref name="Cumplimiento"/>.
/// </param>
/// <param name="Cumplimiento">
/// Los tipos que exige algún Centro donde el Trabajador tiene una Asignación activa, uno por tipo y con el documento
/// efectivo, medidos por <see cref="CumplimientoDocumental"/>: la porción de este Trabajador del % de la Subcontrata
/// (el mismo universo que <c>CalculoEstadoSubcontrataService</c>).
/// </param>
public record TrabajadorDocumentacionSubcontrataDto(
    Guid TrabajadorId, string TrabajadorNombre, string? TrabajadorDni,
    EstadoDocumento PeorEstado, IReadOnlyList<DocumentoRequeridoDto> Documentos,
    FraccionCumplimiento Cumplimiento);

public class ObtenerTrabajadoresDocumentacionPorSubcontrataQueryHandler(
    ITrabajadoresQueryContext trabajadoresContext,
    IAsignacionesQueryContext asignacionesContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IDocumentosQueryContext documentosContext,
    IConfiguracionQueryContext configuracionContext,
    ICentrosQueryContext centrosContext,
    IAlcanceDatosService alcanceDatos,
    IProyectosQueryContext proyectosContext,
    IRequisitosEventoQueryContext requisitosEventoContext)
    : IRequestHandler<ObtenerTrabajadoresDocumentacionPorSubcontrataQuery, IReadOnlyList<TrabajadorDocumentacionSubcontrataDto>>
{
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
            .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos, t.Dni, t.TipoContratacion })
            .ToListAsync(cancellationToken);

        if (trabajadores.Count == 0)
            return [];

        var trabajadorIds = trabajadores.Select(t => t.Id).ToList();
        var tipoContratacionPorTrabajador = trabajadores.ToDictionary(t => t.Id, t => t.TipoContratacion);
        var hoy = DiaDeNegocio.Hoy();

        // Mismo universo que CalculoEstadoSubcontrataService: el voluntario inactivo sigue listado, pero no se le exige
        // nada; y lo que pide cada evento futuro o vigente cuenta para quien está asignado a él (decisión 2026-10-16).
        var voluntariosInactivos = (await ActividadVoluntarios
            .VoluntariosInactivosIds(trabajadoresContext.Trabajadores, proyectosContext, hoy)
            .Where(id => trabajadorIds.Contains(id))
            .ToListAsync(cancellationToken)).ToHashSet();
        var cuentan = trabajadorIds.Where(id => !voluntariosInactivos.Contains(id)).ToList();

        var asignacionesActivas = await asignacionesContext.Asignaciones
            .Where(a => a.FechaBaja == null && cuentan.Contains(a.TrabajadorId))
            .Select(a => new { a.TrabajadorId, a.CentroId })
            .ToListAsync(cancellationToken);

        var requisitosDeEvento = await RequisitosDeEventos
            .DeEventosQueLosAportan(
                trabajadoresContext.Trabajadores, proyectosContext, requisitosEventoContext.ProyectosRequisitos, tiposDocumentoContext.TiposDocumento, hoy)
            .Where(r => cuentan.Contains(r.TrabajadorId))
            .Select(r => new { r.TrabajadorId, r.CentroId, r.TipoDocumentoId })
            .Distinct()
            .ToListAsync(cancellationToken);

        var centrosPorTrabajador = asignacionesActivas
            .GroupBy(a => a.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.Select(a => a.CentroId).Distinct().ToList());

        // P1-X2: un Centro sin gestión CAE no exige ningún tipo a quien trabaja allí, tampoco por sus eventos.
        var sinGestionCae = await CentrosSinGestionCae.FiltrarAsync(
            centrosContext, asignacionesActivas.Select(a => a.CentroId).Concat(requisitosDeEvento.Select(r => r.CentroId)), cancellationToken);

        var tiposDeEventoPorTrabajador = requisitosDeEvento
            .Where(r => !sinGestionCae.Contains(r.CentroId))
            .GroupBy(r => r.TrabajadorId)
            .ToDictionary(g => g.Key, g => g.Select(r => r.TipoDocumentoId).ToHashSet());

        var tiposCandidatos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, CuentaParaCumplimiento = t.Requerido == RequisitoDocumental.Si, t.TiposContratacionDeSerie })
            .ToListAsync(cancellationToken);

        var tiposRequeridosPorTrabajador = new Dictionary<Guid, List<Guid>>();

        if (tiposCandidatos.Count > 0 && (asignacionesActivas.Count > 0 || requisitosDeEvento.Count > 0))
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
                var tiposDeEvento = tiposDeEventoPorTrabajador.GetValueOrDefault(trabajadorId);
                var tipoContratacion = tipoContratacionPorTrabajador[trabajadorId];
                tiposRequeridosPorTrabajador[trabajadorId] = tiposCandidatos
                    .Where(t => tiposDeEvento?.Contains(t.Id) == true
                        || centrosDelTrabajador.Any(centroId => !sinGestionCae.Contains(centroId) && ResolucionTipoDocumentoCentro.Aplica(
                            filasPorPar, t.Id, centroId, t.CuentaParaCumplimiento, t.TiposContratacionDeSerie, tipoContratacion)))
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
            ? new Dictionary<Guid, List<(Guid Id, Guid TipoDocumentoId, EstadoVigenciaDocumento EstadoVigencia, DateOnly? FechaVencimiento, DateOnly FechaEmision, DateTime CreadoEnUtc)>>()
            : (await documentosContext.Documentos.Operativos()
                .Where(d => d.TrabajadorId != null
                    && trabajadorIds.Contains(d.TrabajadorId!.Value)
                    && tipoIdsRequeridosGlobal.Contains(d.TipoDocumentoId))
                .Select(d => new { d.Id, TrabajadorId = d.TrabajadorId!.Value, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc })
                .ToListAsync(cancellationToken))
                .GroupBy(d => d.TrabajadorId)
                .ToDictionary(g => g.Key, g => g.Select(d => (d.Id, d.TipoDocumentoId, d.EstadoVigencia, d.FechaVencimiento, d.FechaEmision, d.CreadoEnUtc)).ToList());

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);

        var resultado = new List<TrabajadorDocumentacionSubcontrataDto>();

        foreach (var trabajador in trabajadores)
        {
            var documentosDelTrabajador = documentosPorTrabajador.GetValueOrDefault(trabajador.Id, []);
            var tipoIdsConDocumento = documentosDelTrabajador.Select(d => d.TipoDocumentoId).ToHashSet();

            var items = new List<DocumentoRequeridoDto>();

            foreach (var documento in documentosDelTrabajador)
            {
                var estado = CalculadoraEstadoDocumento.Calcular(
                    documento.EstadoVigencia, documento.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias);
                // Solo se omite lo confirmado como que no caduca; lo sin confirmar se lista.
                if (estado == EstadoDocumento.SinCaducidad) continue;

                items.Add(new DocumentoRequeridoDto(
                    documento.Id, documento.TipoDocumentoId, nombrePorTipo[documento.TipoDocumentoId], estado, documento.FechaVencimiento));
            }

            foreach (var tipoId in tiposRequeridosPorTrabajador[trabajador.Id])
            {
                if (tipoIdsConDocumento.Contains(tipoId)) continue;
                items.Add(new DocumentoRequeridoDto(null, tipoId, nombrePorTipo[tipoId], EstadoDocumento.Faltante, null));
            }

            var ordenados = items.OrderBy(i => SeveridadEstadoDocumento.Rango(i.Estado)).ThenBy(i => i.TipoDocumentoNombre).ToList();
            var peorEstado = ordenados.Count > 0 ? ordenados[0].Estado : EstadoDocumento.Vigente;

            // Un documento por tipo exigido (el efectivo), igual que el % de la Subcontrata.
            var preferidosPorTipo = DocumentoEfectivo.UnoPorClave(
                documentosDelTrabajador, d => d.TipoDocumentoId, d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, hoy);
            var cumplimiento = CumplimientoDocumental.Evaluar(tiposRequeridosPorTrabajador[trabajador.Id].Select(tipoId =>
                preferidosPorTipo.TryGetValue(tipoId, out var preferido)
                    ? CalculadoraEstadoDocumento.Calcular(
                        preferido.EstadoVigencia, preferido.FechaVencimiento, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias)
                    : EstadoDocumento.Faltante));

            resultado.Add(new TrabajadorDocumentacionSubcontrataDto(
                trabajador.Id, trabajador.Nombre, trabajador.Dni, peorEstado, ordenados, cumplimiento));
        }

        return resultado;
    }
}

using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Configuracion;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Subcontratas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Visitas.Queries.ObtenerDocumentacionVisita;

/// <summary>
/// Alimenta "Detalle de la visita": a diferencia de PestanaDocumentacion
/// (que lista TODOS los Documentos de una Empresa/Trabajador, sin importar
/// si tienen algo que ver con este Centro), aquí solo se muestra lo que
/// aplica al Centro de la visita — un Trabajador puede tener un Documento
/// vencido de un TipoDocumento que este Centro excluye explícitamente
/// (<c>TipoDocumentoCentro.Incluido = false</c>, PLAN-EJECUCION-UX.md § 0.4),
/// y eso no debe interferir en esta vista.
///
/// Incluye "Faltante" (documento obligatorio sin ningún Documento) tanto
/// para Empresa como para Trabajador — a diferencia de ObtenerAlertasQuery/
/// CalculoEstadoCentroService, que solo lo calculan para Trabajador; aquí sí
/// se extiende a Empresa a petición explícita del usuario para esta vista.
///
/// Cada sección viene pre-ordenada por severidad (Faltante, Vencido,
/// Urgente, Próximo, Vigente) y sin NoAplica — es el orden en el que el
/// Gestor quiere verlos, y el peor estado de la sección es el primer
/// elemento tras ordenar.
/// </summary>
public record ObtenerDocumentacionVisitaQuery(Guid VisitaId) : IRequest<DocumentacionVisitaDto?>;

public record DocumentoVisitaItemDto(
    Guid? DocumentoId, Guid? TrabajadorId, Guid TipoDocumentoId, string TipoDocumentoNombre,
    EstadoDocumento Estado, DateOnly? FechaVencimiento, string? ArchivoUrl);

public record SeccionDocumentacionDto(EstadoDocumento PeorEstado, IReadOnlyList<DocumentoVisitaItemDto> Documentos);

public record TrabajadorDocumentacionDto(
    Guid TrabajadorId, string NombreCompleto, string Dni, string EmpleadorNombre, SeccionDocumentacionDto Documentacion);

public record DocumentacionVisitaDto(
    Guid EmpresaId, SeccionDocumentacionDto Empresa, IReadOnlyList<TrabajadorDocumentacionDto> Trabajadores);

public class ObtenerDocumentacionVisitaQueryHandler(
    IVisitasQueryContext visitasContext,
    ICentrosQueryContext centrosContext,
    IDocumentosQueryContext documentosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext,
    IEmpresasQueryContext empresasContext,
    ISubcontratasQueryContext subcontratasContext,
    IConfiguracionQueryContext configuracionContext,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerDocumentacionVisitaQuery, DocumentacionVisitaDto?>
{
    /// <param name="Requerido">
    /// Resolución completa (<see cref="ResolucionTipoDocumentoCentro"/>) — gobierna
    /// solo si se genera el placeholder "Faltante" cuando no hay Documento. El
    /// alcance de qué tipos se muestran en absoluto (incluidos los que ya tienen
    /// Documento) es más amplio, ver <see cref="Handle"/>.
    /// </param>
    private record TipoDocumentoAplicableDto(Guid Id, string Nombre, bool Requerido);

    private static readonly IReadOnlyDictionary<EstadoDocumento, int> OrdenSeveridad = new Dictionary<EstadoDocumento, int>
    {
        [EstadoDocumento.Faltante] = 0,
        [EstadoDocumento.Vencido] = 1,
        [EstadoDocumento.Urgente] = 2,
        [EstadoDocumento.Proximo] = 3,
        [EstadoDocumento.Vigente] = 4
    };

    public async Task<DocumentacionVisitaDto?> Handle(ObtenerDocumentacionVisitaQuery request, CancellationToken cancellationToken)
    {
        var visita = await visitasContext.Visitas
            .Where(v => v.Id == request.VisitaId)
            .Select(v => new { v.Id, v.CentroId })
            .FirstOrDefaultAsync(cancellationToken);

        if (visita is null) return null;
        if (!await alcanceDatos.CentroVisibleAsync(visita.CentroId, cancellationToken)) return null;

        var centro = await centrosContext.Centros
            .Where(c => c.Id == visita.CentroId)
            .Select(c => new { c.Id, c.EmpresaId })
            .FirstOrDefaultAsync(cancellationToken);

        if (centro is null) return null;

        var trabajadorIds = await visitasContext.VisitasTrabajadores
            .Where(vt => vt.VisitaId == request.VisitaId)
            .Select(vt => vt.TrabajadorId)
            .ToListAsync(cancellationToken);

        var parametros = await configuracionContext.ParametrosSistema.SingleAsync(cancellationToken);
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        var tipos = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa || t.AmbitoAplicacion == AmbitoAplicacion.Trabajador)
            .Select(t => new { t.Id, t.Nombre, t.AmbitoAplicacion, t.EsObligatorio })
            .ToListAsync(cancellationToken);

        var tipoIdsRelevantes = tipos.Select(t => t.Id).ToHashSet();
        var filasDelCentro = (await tiposDocumentoContext.TiposDocumentoCentros
            .Where(tc => tipoIdsRelevantes.Contains(tc.TipoDocumentoId) && tc.CentroId == centro.Id)
            .ToListAsync(cancellationToken))
            .ToDictionary(tc => (tc.TipoDocumentoId, tc.CentroId));

        // Alcance de qué tipos entran en esta vista en absoluto (incluye los que ya
        // tienen Documento, no solo los obligatorios) — por defecto todos entran,
        // salvo que este Centro los excluya explícitamente (Incluido=false). Más
        // permisivo a propósito que ResolucionTipoDocumentoCentro.Aplica (que exige
        // EsObligatorio sin fila explícita): un Documento Vigente de un tipo no
        // obligatorio sigue siendo información relevante para la visita.
        bool EnAlcanceDelCentro(Guid tipoDocumentoId) =>
            !filasDelCentro.TryGetValue((tipoDocumentoId, centro.Id), out var fila) || fila.Incluido;

        var tiposEmpresa = tipos
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Empresa && EnAlcanceDelCentro(t.Id))
            .Select(t => new TipoDocumentoAplicableDto(t.Id, t.Nombre, ResolucionTipoDocumentoCentro.Aplica(filasDelCentro, t.Id, centro.Id, t.EsObligatorio)))
            .ToList();
        var tiposTrabajador = tipos
            .Where(t => t.AmbitoAplicacion == AmbitoAplicacion.Trabajador && EnAlcanceDelCentro(t.Id))
            .Select(t => new TipoDocumentoAplicableDto(t.Id, t.Nombre, ResolucionTipoDocumentoCentro.Aplica(filasDelCentro, t.Id, centro.Id, t.EsObligatorio)))
            .ToList();

        var seccionEmpresa = await ConstruirSeccionAsync(
            documentosContext.Documentos.Where(d => d.EmpresaId == centro.EmpresaId),
            tiposEmpresa, trabajadorId: null, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, cancellationToken);

        var trabajadores = await ConstruirTrabajadoresAsync(
            trabajadorIds, tiposTrabajador, hoy, parametros.UmbralAmbarDias, parametros.UmbralRojoDias, cancellationToken);

        return new DocumentacionVisitaDto(centro.EmpresaId, seccionEmpresa, trabajadores);
    }

    private async Task<SeccionDocumentacionDto> ConstruirSeccionAsync(
        IQueryable<Documento> documentosDelPropietario,
        IReadOnlyList<TipoDocumentoAplicableDto> tiposAplicables,
        Guid? trabajadorId,
        DateOnly hoy, int umbralAmbarDias, int umbralRojoDias,
        CancellationToken cancellationToken)
    {
        var tiposPorId = tiposAplicables.ToDictionary(t => t.Id);
        if (tiposPorId.Count == 0)
            return new SeccionDocumentacionDto(EstadoDocumento.Vigente, []);

        var documentos = await documentosDelPropietario
            .Where(d => tiposPorId.Keys.Contains(d.TipoDocumentoId))
            .Select(d => new { d.Id, d.TipoDocumentoId, d.FechaVencimiento, d.ArchivoUrl })
            .ToListAsync(cancellationToken);

        var tipoIdsConDocumento = documentos.Select(d => d.TipoDocumentoId).ToHashSet();

        var items = new List<DocumentoVisitaItemDto>();

        foreach (var documento in documentos)
        {
            var estado = CalculadoraEstadoDocumento.Calcular(documento.FechaVencimiento, hoy, umbralAmbarDias, umbralRojoDias);
            if (estado == EstadoDocumento.NoAplica) continue;

            items.Add(new DocumentoVisitaItemDto(
                documento.Id, trabajadorId, documento.TipoDocumentoId, tiposPorId[documento.TipoDocumentoId].Nombre,
                estado, documento.FechaVencimiento, documento.ArchivoUrl));
        }

        foreach (var tipo in tiposAplicables)
        {
            if (!tipo.Requerido) continue;
            if (tipoIdsConDocumento.Contains(tipo.Id)) continue;

            items.Add(new DocumentoVisitaItemDto(null, trabajadorId, tipo.Id, tipo.Nombre, EstadoDocumento.Faltante, null, null));
        }

        var ordenados = items.OrderBy(i => OrdenSeveridad[i.Estado]).ThenBy(i => i.TipoDocumentoNombre).ToList();
        var peorEstado = ordenados.Count > 0 ? ordenados[0].Estado : EstadoDocumento.Vigente;

        return new SeccionDocumentacionDto(peorEstado, ordenados);
    }

    private async Task<IReadOnlyList<TrabajadorDocumentacionDto>> ConstruirTrabajadoresAsync(
        IReadOnlyList<Guid> trabajadorIds, IReadOnlyList<TipoDocumentoAplicableDto> tiposTrabajador,
        DateOnly hoy, int umbralAmbarDias, int umbralRojoDias, CancellationToken cancellationToken)
    {
        if (trabajadorIds.Count == 0) return [];

        var trabajadores = await (
            from trabajador in trabajadoresContext.Trabajadores
            where trabajadorIds.Contains(trabajador.Id)
            join empresa in empresasContext.Empresas on trabajador.EmpresaId equals empresa.Id into empresasCoincidentes
            from empresa in empresasCoincidentes.DefaultIfEmpty()
            join subcontrata in subcontratasContext.Subcontratas on trabajador.SubcontrataId equals subcontrata.Id into subcontratasCoincidentes
            from subcontrata in subcontratasCoincidentes.DefaultIfEmpty()
            orderby trabajador.Apellidos, trabajador.Nombre
            select new
            {
                trabajador.Id,
                Nombre = trabajador.Nombre + " " + trabajador.Apellidos,
                trabajador.Dni,
                EmpleadorNombre = empresa != null ? empresa.RazonSocial : (subcontrata != null ? subcontrata.RazonSocial : "—")
            })
            .ToListAsync(cancellationToken);

        var resultado = new List<TrabajadorDocumentacionDto>();
        foreach (var trabajador in trabajadores)
        {
            var seccion = await ConstruirSeccionAsync(
                documentosContext.Documentos.Where(d => d.TrabajadorId == trabajador.Id),
                tiposTrabajador, trabajador.Id, hoy, umbralAmbarDias, umbralRojoDias, cancellationToken);

            resultado.Add(new TrabajadorDocumentacionDto(trabajador.Id, trabajador.Nombre, trabajador.Dni, trabajador.EmpleadorNombre, seccion));
        }

        return resultado;
    }
}

using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.HerramientasPdf;

/// <summary>
/// Los Documentos de una Empresa gestionada que se pueden unir: sus archivos vigentes y los de sus Trabajadores. Solo
/// lista; leer el contenido y dejar constancia de ello es de <see cref="UnirPdfQuery"/>.
/// </summary>
public record ListarDocumentosParaUnirQuery(Guid EmpresaGestionadaId) : IRequest<Result<IReadOnlyList<DocumentoParaUnirDto>>>;

/// <param name="TitularNombre">El Trabajador, si el Documento es suyo; <c>null</c> si es de la propia Empresa.</param>
public record DocumentoParaUnirDto(
    Guid Id, string TipoNombre, string? TitularNombre, DateOnly FechaEmision, DateOnly? FechaVencimiento);

/// <summary>
/// Qué Documentos entran en el universo de Unir. La misma definición que el paquete documental de Empresa 360: Documentos
/// operativos con archivo, de la Empresa (no de un Vehículo ni de un Trabajador) o de sus Trabajadores propios que el
/// alcance del usuario deja ver. Lo comparten el listado y la unión para que no puedan divergir.
/// </summary>
internal static class DocumentosUnibles
{
    /// <summary>Tope del listado de la pantalla; por encima se pide acotar la Empresa, no se pagina.</summary>
    public const int MaximoListados = 500;

    // Los mismos literales que AutorizacionPaqueteEmpresa y AutorizacionEscrituraBehavior: Application no referencia
    // Infrastructure.Identity.Roles.
    private static readonly string[] RolesQueLeenDocumentos = ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    /// <summary>
    /// Código de rechazo si quien pide no puede leer Documentos de un Tenant para sacarlos de TALVEG; <c>null</c> si puede. Una
    /// Sesión Privilegiada de plataforma no lee (decisión provisional del paquete de Empresa 360) y solo leen los roles que
    /// escriben. Lo aplican el listado y la unión: el listado entrega nombres de titulares y tipos de Documento.
    /// </summary>
    public static async Task<Error?> RechazoDeLecturaAsync(
        ISesionPrivilegiadaActual sesionPrivilegiadaActual, ICurrentUserService currentUserService, CancellationToken cancellationToken)
    {
        if (await sesionPrivilegiadaActual.ObtenerAsync(cancellationToken) is not null)
            return Error.Crear(ErroresUnirPdf.SesionPrivilegiadaSinLectura, "Una sesión privilegiada de plataforma no puede leer documentos para unirlos.");
        if (await currentUserService.ObtenerRolEfectivoAsync() is not { } rol || !RolesQueLeenDocumentos.Contains(rol))
            return Error.Crear(ErroresUnirPdf.RolSinAcceso, "Tu rol no permite leer documentos para unirlos.");
        return null;
    }

    public static async Task<List<Guid>> TrabajadorIdsAsync(
        Guid empresaId, ITrabajadoresQueryContext trabajadoresContext, IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var consulta = trabajadoresContext.Trabajadores.Where(t => t.EmpresaId == empresaId);
        if (visibles is not null)
            consulta = consulta.Where(t => visibles.Contains(t.Id));
        return await consulta.Select(t => t.Id).ToListAsync(cancellationToken);
    }

    public static IQueryable<Documento> De(IDocumentosQueryContext documentosContext, Guid empresaId, List<Guid> trabajadorIds) =>
        documentosContext.Documentos.Operativos()
            .Where(d => d.ArchivoUrl != null && !d.EsRenuncia
                && ((d.EmpresaId == empresaId && d.TrabajadorId == null && d.VehiculoId == null)
                    || (d.TrabajadorId != null && trabajadorIds.Contains(d.TrabajadorId.Value))));

    /// <summary>La Empresa existe, es propia del Tenant y está en la cartera de quien pregunta; si no, se responde igual que si no existiera.</summary>
    public static async Task<Empresa?> EmpresaGestionableAsync(
        Guid empresaId, IEmpresasQueryContext empresasContext, IAlcanceDatosService alcanceDatos, CancellationToken cancellationToken)
    {
        if (!await alcanceDatos.EmpresaParaGestionVisibleAsync(empresaId, cancellationToken))
            return null;

        return await empresasContext.Empresas.Where(e => e.Id == empresaId && e.EsPropia).FirstOrDefaultAsync(cancellationToken);
    }
}

public class ListarDocumentosParaUnirQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad,
    IEmpresasQueryContext empresasContext,
    IDocumentosQueryContext documentosContext,
    ITrabajadoresQueryContext trabajadoresContext,
    ITiposDocumentoQueryContext tiposContext,
    IProyectosQueryContext proyectosContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    ISesionPrivilegiadaActual sesionPrivilegiadaActual)
    : IRequestHandler<ListarDocumentosParaUnirQuery, Result<IReadOnlyList<DocumentoParaUnirDto>>>
{
    public async Task<Result<IReadOnlyList<DocumentoParaUnirDto>>> Handle(
        ListarDocumentosParaUnirQuery request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Result.Fallo<IReadOnlyList<DocumentoParaUnirDto>>(Error.Crear(ErroresUnirPdf.NoDisponible, "Herramientas PDF no está disponible."));

        if (await DocumentosUnibles.EmpresaGestionableAsync(request.EmpresaGestionadaId, empresasContext, alcanceDatos, cancellationToken) is null)
            return Result.Fallo<IReadOnlyList<DocumentoParaUnirDto>>(Error.Crear(
                ErroresUnirPdf.EmpresaNoGestionable, "La Empresa gestionada no existe o queda fuera de tu cartera."));

        if (await DocumentosUnibles.RechazoDeLecturaAsync(sesionPrivilegiadaActual, currentUserService, cancellationToken) is { } rechazo)
            return Result.Fallo<IReadOnlyList<DocumentoParaUnirDto>>(rechazo);

        var trabajadorIds = await DocumentosUnibles.TrabajadorIdsAsync(request.EmpresaGestionadaId, trabajadoresContext, alcanceDatos, cancellationToken);
        var candidatos = await DocumentosUnibles.De(documentosContext, request.EmpresaGestionadaId, trabajadorIds)
            .OrderByDescending(d => d.FechaEmision).ThenBy(d => d.Id)
            .Take(DocumentosUnibles.MaximoListados)
            .Select(d => new { d.Id, d.TipoDocumentoId, d.TrabajadorId, d.FechaEmision, d.FechaVencimiento })
            .ToListAsync(cancellationToken);

        // La misma puerta de lectura que abre un Documento suelto: lo que no se puede leer no se ofrece.
        var legibles = await alcanceDatos.DocumentosLegiblesAsync(
            candidatos.Select(d => d.Id).ToList(), documentosContext, proyectosContext, cancellationToken);
        candidatos = candidatos.Where(d => legibles.Contains(d.Id)).ToList();

        var tipoIds = candidatos.Select(d => d.TipoDocumentoId).Distinct().ToList();
        var tipos = await tiposContext.TiposDocumento
            .Where(t => tipoIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        var titularIds = candidatos.Where(d => d.TrabajadorId is not null).Select(d => d.TrabajadorId!.Value).Distinct().ToList();
        var titulares = titularIds.Count == 0
            ? []
            : await trabajadoresContext.Trabajadores
                .Where(t => titularIds.Contains(t.Id))
                .Select(t => new { t.Id, Nombre = t.Nombre + " " + t.Apellidos })
                .ToDictionaryAsync(t => t.Id, t => t.Nombre, cancellationToken);

        IReadOnlyList<DocumentoParaUnirDto> lista = candidatos
            .Select(d => new DocumentoParaUnirDto(
                d.Id,
                tipos.GetValueOrDefault(d.TipoDocumentoId, "Documento"),
                d.TrabajadorId is { } trabajadorId ? titulares.GetValueOrDefault(trabajadorId) : null,
                d.FechaEmision, d.FechaVencimiento))
            .ToList();

        return Result.Exito(lista);
    }
}

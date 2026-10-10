using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Documentos.Queries.ObtenerDocumentos;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Reclamaciones;
using CaeManager.Domain.Comunicaciones;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.SituacionEnCentro;

/// <summary>
/// La última vez que se reclamó un documento (o un documento que falta).
/// </summary>
/// <param name="SinRespuesta">
/// Mismo criterio que la pestaña de reclamaciones enviadas (<see cref="RespuestaDeReclamacion"/>): <c>null</c> si el
/// envío no tiene Conversación.
/// </param>
public record UltimaReclamacionDocumentoDto(DateTime FechaEnvioUtc, bool? SinRespuesta);

/// <summary>
/// Lo que las fichas 360 enseñan en la segunda línea de cada documento cuando el contexto fija un Centro de
/// Trabajo: su estado en las plataformas de ESE Centro y la última reclamación. Se carga de una vez para toda la
/// lista (<see cref="ISituacionDocumentosEnCentrosService"/>) y se consulta en memoria fila a fila.
/// </summary>
public sealed class SituacionDocumentosEnCentros
{
    public static readonly SituacionDocumentosEnCentros Vacia = new(
        new Dictionary<(Guid, Guid), IReadOnlyList<AcreditacionResumenDto>>(),
        new Dictionary<Guid, UltimaReclamacionDocumentoDto>(),
        new Dictionary<(Guid, Guid), UltimaReclamacionDocumentoDto>());

    private readonly IReadOnlyDictionary<(Guid CentroId, Guid DocumentoId), IReadOnlyList<AcreditacionResumenDto>> _acreditaciones;
    private readonly IReadOnlyDictionary<Guid, UltimaReclamacionDocumentoDto> _reclamacionesPorDocumento;
    private readonly IReadOnlyDictionary<(Guid TrabajadorId, Guid TipoDocumentoId), UltimaReclamacionDocumentoDto> _reclamacionesDeAusentes;

    public SituacionDocumentosEnCentros(
        IReadOnlyDictionary<(Guid CentroId, Guid DocumentoId), IReadOnlyList<AcreditacionResumenDto>> acreditaciones,
        IReadOnlyDictionary<Guid, UltimaReclamacionDocumentoDto> reclamacionesPorDocumento,
        IReadOnlyDictionary<(Guid TrabajadorId, Guid TipoDocumentoId), UltimaReclamacionDocumentoDto> reclamacionesDeAusentes)
    {
        _acreditaciones = acreditaciones;
        _reclamacionesPorDocumento = reclamacionesPorDocumento;
        _reclamacionesDeAusentes = reclamacionesDeAusentes;
    }

    /// <summary>
    /// Las acreditaciones del documento en los canales de plataforma de ese Centro, el principal primero.
    /// <c>null</c> si no hay ninguna (el Centro no tiene plataforma, o el documento no se acredita en ella).
    /// </summary>
    public IReadOnlyList<AcreditacionResumenDto>? AcreditacionesEn(Guid centroId, Guid documentoId) =>
        _acreditaciones.GetValueOrDefault((centroId, documentoId));

    public UltimaReclamacionDocumentoDto? UltimaReclamacionDe(Guid documentoId) =>
        _reclamacionesPorDocumento.GetValueOrDefault(documentoId);

    /// <summary>La última reclamación de un documento que falta: el tipo que se le pidió a ese Trabajador.</summary>
    public UltimaReclamacionDocumentoDto? UltimaReclamacionDeAusente(Guid trabajadorId, Guid tipoDocumentoId) =>
        _reclamacionesDeAusentes.GetValueOrDefault((trabajadorId, tipoDocumentoId));
}

public interface ISituacionDocumentosEnCentrosService
{
    /// <summary>
    /// Un número fijo de consultas para toda la lista, sin importar cuántos documentos tenga. Quien llama ya ha
    /// comprobado que el usuario ve esos Centros y esos documentos. Las reclamaciones se acotan además aquí: solo las
    /// leen los roles de gestión documental (los mismos de la pestaña Reclamaciones de Documentos; ni Consulta ni el
    /// Usuario de Cliente), y solo las de los titulares que el usuario puede gestionar.
    /// </summary>
    /// <param name="trabajadorIdsConAusentes">Trabajadores a los que les falta algún documento exigido.</param>
    /// <param name="tipoDocumentoIdsDeAusentes">Tipos de documento que pueden faltar.</param>
    Task<SituacionDocumentosEnCentros> CargarAsync(
        IReadOnlyCollection<Guid> centroIds,
        IReadOnlyCollection<Guid> documentoIds,
        IReadOnlyCollection<Guid> trabajadorIdsConAusentes,
        IReadOnlyCollection<Guid> tipoDocumentoIdsDeAusentes,
        CancellationToken cancellationToken);
}

public class SituacionDocumentosEnCentrosService(
    IDocumentosQueryContext documentosContext,
    ICentrosQueryContext centrosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext,
    IReclamacionesQueryContext reclamacionesContext,
    IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService) : ISituacionDocumentosEnCentrosService
{
    // Quién lee el historial de reclamaciones: mismos roles que la pestaña Reclamaciones de Documentos
    // (Documentos.RolesDeGestionDocumental). A Consulta y al Usuario de Cliente esa pestaña les dice «sin permiso»;
    // la segunda línea de una ficha no puede ser una puerta lateral al mismo dato.
    private static readonly string[] RolesQueLeenReclamaciones =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    public async Task<SituacionDocumentosEnCentros> CargarAsync(
        IReadOnlyCollection<Guid> centroIds,
        IReadOnlyCollection<Guid> documentoIds,
        IReadOnlyCollection<Guid> trabajadorIdsConAusentes,
        IReadOnlyCollection<Guid> tipoDocumentoIdsDeAusentes,
        CancellationToken cancellationToken)
    {
        var acreditaciones = await CargarAcreditacionesAsync(centroIds, documentoIds, cancellationToken);
        var (porDocumento, deAusentes) = await CargarReclamacionesAsync(
            documentoIds, trabajadorIdsConAusentes, tipoDocumentoIdsDeAusentes, cancellationToken);

        return new SituacionDocumentosEnCentros(acreditaciones, porDocumento, deAusentes);
    }

    private async Task<Dictionary<(Guid, Guid), IReadOnlyList<AcreditacionResumenDto>>> CargarAcreditacionesAsync(
        IReadOnlyCollection<Guid> centroIds, IReadOnlyCollection<Guid> documentoIds, CancellationToken cancellationToken)
    {
        if (centroIds.Count == 0 || documentoIds.Count == 0) return [];

        // Mismo dato que los badges de /documentos (ObtenerDocumentosQuery), acotado a los canales de estos Centros:
        // la acreditación es por documento × canal, y el canal es de un Centro.
        var crudas = await (
            from acreditacion in documentosContext.AcreditacionesDocumentoPlataforma
            join canal in centrosContext.CanalesGestionDocumental on acreditacion.CanalGestionDocumentalId equals canal.Id
            where documentoIds.Contains(acreditacion.DocumentoId) && centroIds.Contains(canal.CentroId)
            select new
            {
                acreditacion.DocumentoId,
                acreditacion.Id,
                acreditacion.Estado,
                canal.CentroId,
                canal.EsPrincipal,
                canal.ProveedorPlataformaCaeId
            })
            .ToListAsync(cancellationToken);

        if (crudas.Count == 0) return [];

        var proveedorIds = crudas.Where(c => c.ProveedorPlataformaCaeId is not null)
            .Select(c => c.ProveedorPlataformaCaeId!.Value).Distinct().ToList();
        var nombresProveedor = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        return crudas
            .Select(c => new
            {
                c.CentroId,
                c.DocumentoId,
                c.EsPrincipal,
                Resumen = new AcreditacionResumenDto(
                    c.Id,
                    c.ProveedorPlataformaCaeId is { } id ? nombresProveedor.GetValueOrDefault(id, "Plataforma") : "Plataforma",
                    c.Estado)
            })
            .GroupBy(c => (c.CentroId, c.DocumentoId))
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<AcreditacionResumenDto>)g
                    .OrderByDescending(c => c.EsPrincipal)
                    .ThenBy(c => c.Resumen.NombrePlataforma, StringComparer.CurrentCulture)
                    .ThenBy(c => c.Resumen.Id)
                    .Select(c => c.Resumen)
                    .ToList());
    }

    private async Task<(Dictionary<Guid, UltimaReclamacionDocumentoDto> PorDocumento,
        Dictionary<(Guid, Guid), UltimaReclamacionDocumentoDto> DeAusentes)> CargarReclamacionesAsync(
        IReadOnlyCollection<Guid> documentoIds,
        IReadOnlyCollection<Guid> trabajadorIdsConAusentes,
        IReadOnlyCollection<Guid> tipoDocumentoIdsDeAusentes,
        CancellationToken cancellationToken)
    {
        var buscaAusentes = trabajadorIdsConAusentes.Count > 0 && tipoDocumentoIdsDeAusentes.Count > 0;
        if (documentoIds.Count == 0 && !buscaAusentes) return ([], []);

        if (await currentUserService.ObtenerRolEfectivoAsync() is not { } rol || !RolesQueLeenReclamaciones.Contains(rol))
            return ([], []);

        // Mismo alcance que la pestaña de reclamaciones enviadas: lo que se le ha reclamado a un titular es de
        // gestión, no contenido de portal. Ver un documento no da derecho a ver a quién se le reclamó.
        var titularesClienteVisibles = await alcanceDatos.ObtenerClienteIdsVisiblesAsync(cancellationToken);
        var titularesEmpresaVisibles = await alcanceDatos.ObtenerEmpresaIdsParaGestionAsync(cancellationToken);

        var lineas = await (
            from linea in reclamacionesContext.ReclamacionesDocumentalesDocumento
            join reclamacion in reclamacionesContext.ReclamacionesDocumentales on linea.ReclamacionDocumentalId equals reclamacion.Id
            where ((reclamacion.ClienteId != null &&
                    (titularesClienteVisibles == null || titularesClienteVisibles.Contains(reclamacion.ClienteId!.Value)))
                || (reclamacion.EmpresaId != null &&
                    (titularesEmpresaVisibles == null || titularesEmpresaVisibles.Contains(reclamacion.EmpresaId!.Value))))
               && ((linea.DocumentoId != null && documentoIds.Contains(linea.DocumentoId!.Value))
                || (buscaAusentes
                    && linea.DocumentoId == null
                    && linea.TrabajadorId != null && trabajadorIdsConAusentes.Contains(linea.TrabajadorId!.Value)
                    && linea.TipoDocumentoId != null && tipoDocumentoIdsDeAusentes.Contains(linea.TipoDocumentoId!.Value)))
            select new
            {
                linea.DocumentoId,
                linea.TrabajadorId,
                linea.TipoDocumentoId,
                ReclamacionId = reclamacion.Id,
                reclamacion.FechaEnvioUtc,
                reclamacion.ConversacionId
            })
            .ToListAsync(cancellationToken);

        if (lineas.Count == 0) return ([], []);

        // La última por clave; a igual instante, la de Id mayor, para que el resultado no dependa del orden de lectura.
        var ultimasPorDocumento = lineas
            .Where(l => l.DocumentoId is not null)
            .GroupBy(l => l.DocumentoId!.Value)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FechaEnvioUtc).ThenByDescending(l => l.ReclamacionId).First());
        var ultimasDeAusentes = lineas
            .Where(l => l.DocumentoId is null && l.TrabajadorId is not null && l.TipoDocumentoId is not null)
            .GroupBy(l => (l.TrabajadorId!.Value, l.TipoDocumentoId!.Value))
            .ToDictionary(g => g.Key, g => g.OrderByDescending(l => l.FechaEnvioUtc).ThenByDescending(l => l.ReclamacionId).First());

        var conversacionIds = ultimasPorDocumento.Values.Concat(ultimasDeAusentes.Values)
            .Where(l => l.ConversacionId is not null)
            .Select(l => l.ConversacionId!.Value)
            .Distinct()
            .ToList();
        var entrantesPorConversacion = conversacionIds.Count == 0
            ? []
            : (await comunicacionesContext.Mensajes
                .Where(m => conversacionIds.Contains(m.ConversacionId) && m.Direccion == DireccionMensaje.Entrante)
                .Select(m => new { m.ConversacionId, m.FechaUtc })
                .ToListAsync(cancellationToken))
                .GroupBy(m => m.ConversacionId)
                .ToDictionary(g => g.Key, g => g.Select(m => m.FechaUtc).ToList());

        UltimaReclamacionDocumentoDto ADto(DateTime fechaEnvioUtc, Guid? conversacionId) => new(
            fechaEnvioUtc,
            RespuestaDeReclamacion.SinRespuesta(
                conversacionId,
                fechaEnvioUtc,
                conversacionId is { } id ? entrantesPorConversacion.GetValueOrDefault(id, []) : []));

        return (
            ultimasPorDocumento.ToDictionary(p => p.Key, p => ADto(p.Value.FechaEnvioUtc, p.Value.ConversacionId)),
            ultimasDeAusentes.ToDictionary(p => p.Key, p => ADto(p.Value.FechaEnvioUtc, p.Value.ConversacionId)));
    }
}

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
    ICurrentUserService currentUserService,
    IDirectorioUsuariosService? directorioUsuarios = null) : ISituacionDocumentosEnCentrosService
{
    // Quién lee el historial de reclamaciones: mismos roles que la pestaña Reclamaciones de Documentos
    // (Documentos.RolesDeGestionDocumental). A Consulta y al Usuario de Cliente esa pestaña les dice «sin permiso»;
    // la segunda línea de una ficha no puede ser una puerta lateral al mismo dato. Con la misma regla se leen la nota, el
    // motivo y el Gestor CAE de un cambio de estado anotado a mano en la plataforma (Centro 360, bloque 3 del 21-10).
    //
    // Regla de trazabilidad interna del rol Consulta (opción C, DECISION_LOG 2026-11-14, «Tanda 1 de decisiones
    // pendientes», punto 1; los rastros de reclamaciones y el motivo de la vista rápida, de la misma fecha, #2233). Son DOS
    // reglas por tipo de dato y no se unifican: (1) la información operativa (el último motivo de rechazo del historial de
    // rechazos de la acreditación, la dirección del portal y las notas del canal) la lee Consulta, porque su puerta es
    // IAlcanceDatosService.ObtenerCentroIdsParaGestionAsync, que solo excluye al rol Cliente; (2) la trazabilidad interna,
    // de la que Consulta y el Usuario de Cliente quedan fuera. Esta lista la aplica el servidor en cinco puntos:
    //   - la nota, el motivo y el Gestor CAE autor del REGISTRO de un cambio de estado anotado a mano
    //     (CargarAnotacionesAsync de este servicio y ObtenerCambiosEstadoPlataformaDeCentroQuery);
    //   - la última reclamación de los documentos de la situación, presentes o ausentes (CargarReclamacionesAsync);
    //   - ObtenerReclamacionesSinRespuestaQuery, que devuelve vacío (bloque «Reclamado y sin respuesta» de Inicio);
    //   - ObtenerUltimaReclamacionClienteQuery, que devuelve null (línea «Última reclamación al Cliente» del Centro en el
    //     Context Workspace);
    //   - el UltimoMotivoRechazo de ObtenerAcreditacionesDelDocumentoQuery (vista rápida de un Documento), que queda a null.
    // La presentación sigue la misma lista (RolesComunicaciones.LecturaReclamaciones): Inicio y CentroWorkspacePanel no envían
    // esas dos Queries a los demás roles y DocumentoWorkspacePanel no pinta el motivo.
    // El corte del motivo es solo el de la vista rápida: el motivo de un rechazo NO queda en ese único punto, y el de uno
    // anotado a mano tampoco queda solo en el registro del cambio. AcreditacionDocumentoPlataforma.CambiarEstadoAMano lo
    // añade también al historial de rechazos (causa «Otro», sin autor ni nota; no distingue su origen del de un rechazo de la
    // Plataforma CAE), y de ahí le sigue llegando a Consulta como UltimoMotivoRechazo por ObtenerDocumentosQuery (listado de
    // Documentos, solo si la acreditación está rechazada) y por ObtenerAcreditacionesPorProveedorQuery, sin condicionarlo al
    // estado actual (pestaña Plataformas de Empresa 360, donde FilasPlataformasEmpresa lo filtra a las rechazadas en la
    // presentación, y GET /extension/acreditaciones-pendientes, que solo exige sesión o token de extensión, sin lista de
    // roles, y donde la extensión excluye los rechazos vigentes, así que viaja el de rechazos pasados).
    // El historial de reclamaciones enviadas (ObtenerReclamacionesEnviadasQuery) NO la comprueba en el servidor: acota por
    // cartera, no por rol, y se le corta a Consulta solo en la presentación (Documentos.RolesDeGestionDocumental y
    // RolesComunicaciones.LecturaReclamaciones; HistorialReclamacionesEnComunicacionesTests, en la Web, fija que esas dos
    // listas no se separen de esta).
    // Decide el rol efectivo (ICurrentUserService.ObtenerRolEfectivoAsync), así que la regla no depende de la vía por la
    // que la sesión es Consulta (su Tenant de origen, un Tenant beneficiario delegado por Asignación de Operación o la
    // delegación heredada del acceso de soporte).
    // El Usuario de Cliente sigue con la decisión del 2026-11-12 («B: estado, sin motivo»).
    internal static readonly string[] RolesQueLeenReclamaciones =
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
                canal.ProveedorPlataformaCaeId,
                acreditacion.UltimoCambioEstadoAManoId
            })
            .ToListAsync(cancellationToken);

        if (crudas.Count == 0) return [];

        var proveedorIds = crudas.Where(c => c.ProveedorPlataformaCaeId is not null)
            .Select(c => c.ProveedorPlataformaCaeId!.Value).Distinct().ToList();
        var nombresProveedor = await proveedoresContext.ProveedoresPlataformaCae
            .Where(p => proveedorIds.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Nombre, cancellationToken);

        var anotaciones = await CargarAnotacionesAsync(
            crudas.Where(c => c.UltimoCambioEstadoAManoId is not null).Select(c => c.UltimoCambioEstadoAManoId!.Value).ToList(),
            cancellationToken);

        return crudas
            .Select(c => new
            {
                c.CentroId,
                c.DocumentoId,
                c.EsPrincipal,
                Resumen = new AcreditacionResumenDto(
                    c.Id,
                    c.ProveedorPlataformaCaeId is { } id ? nombresProveedor.GetValueOrDefault(id, "Plataforma") : "Plataforma",
                    c.Estado,
                    anotaciones.GetValueOrDefault(c.Id))
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

    /// <summary>
    /// El cambio anotado a mano que dejó el estado actual de cada acreditación (<c>UltimoCambioEstadoAManoId</c>): si otra
    /// vía tocó el estado después, o se renovó el Documento, la acreditación ya no lo apunta y no se anota nada, aunque
    /// el estado haya vuelto a coincidir: ese texto sería de otro estado o de otra versión del documento. El nombre del usuario se resuelve con el mismo acotado que
    /// el resto de nombres: quien no es visible desde el Tenant operado sale sin nombre.
    /// </summary>
    private async Task<Dictionary<Guid, AnotacionEstadoPlataformaDto>> CargarAnotacionesAsync(
        IReadOnlyList<Guid> cambioIds, CancellationToken cancellationToken)
    {
        if (cambioIds.Count == 0) return [];

        var vigentes = await documentosContext.CambiosEstadoAcreditacionPlataforma
            .Where(c => cambioIds.Contains(c.Id) && c.DeshechoUtc == null)
            .Select(c => new { c.Id, c.AcreditacionId, c.Fecha, c.Motivo, c.Nota, c.UsuarioId })
            .ToListAsync(cancellationToken);
        if (vigentes.Count == 0) return [];

        // Fuera de los roles de gestión documental (Consulta, Usuario de Cliente) solo sale la fecha: la nota, el motivo y
        // quién lo anotó son de gestión, no contenido de portal.
        var leeDetalle = await currentUserService.ObtenerRolEfectivoAsync() is { } rol && RolesQueLeenReclamaciones.Contains(rol);
        var usuarioIds = !leeDetalle ? [] : vigentes.Where(c => c.UsuarioId is not null).Select(c => c.UsuarioId!.Value).Distinct().ToList();
        var nombres = usuarioIds.Count == 0 || directorioUsuarios is null
            ? new Dictionary<Guid, string>()
            : await directorioUsuarios.ObtenerNombresVisiblesAsync(usuarioIds, cancellationToken);

        return vigentes.ToDictionary(
            c => c.AcreditacionId,
            c => new AnotacionEstadoPlataformaDto(
                c.Id, c.Fecha, leeDetalle ? c.Motivo : null, leeDetalle ? c.Nota : null,
                leeDetalle && c.UsuarioId is { } usuarioId ? nombres.GetValueOrDefault(usuarioId) : null));
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

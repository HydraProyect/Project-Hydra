using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.DocumentosIa;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;

/// <summary>
/// Revisiones IA pendientes (sin resolver), acotadas al alcance de cartera
/// del usuario actual — ver VerificacionIaDocumentoService y
/// ValidacionDocumentoOficialService. Desde la validación de documentos
/// oficiales, una revisión puede pertenecer a un Documento de Empresa, no
/// solo de Trabajador — por eso el join con Trabajador es left join.
/// </summary>
public record ObtenerRevisionesIaPendientesQuery : IRequest<IReadOnlyList<RevisionIaDocumentoDto>>;

/// <param name="PropietarioNombre">El trabajador ("Nombre Apellidos") o la razón social de la Empresa, según el ámbito del Documento.</param>
/// <param name="VigenciaLaFijaElTipo">El tipo calcula el vencimiento desde la emisión (<c>TipoDocumento.FijaVigenciaDesdeLaEmision</c>): condición para aceptar la lectura en bloque.</param>
/// <param name="ClienteId">
/// Solo para revisiones de Documento de Trabajador — Cliente "principal" del
/// Trabajador (<see cref="IResolverClientePrincipalService"/>), para la
/// agrupación "por situación" del rediseño de Inicio (hallazgo P-03 de la
/// auditoría de producto 2026-08-16). Null en revisiones de Documento de
/// Empresa: ahí <paramref name="EmpresaId"/> ya es el grupo natural.
/// </param>
public record RevisionIaDocumentoDto(
    Guid Id,
    Guid DocumentoId,
    string PropietarioNombre,
    string TipoDocumentoNombre,
    int ConfianzaGeneral,
    string? TipoDetectado,
    DateOnly? FechaEmisionDetectada,
    string Motivo,
    DateTime CreadaEnUtc,
    Guid? TrabajadorId = null,
    Guid? EmpresaId = null,
    Guid? ClienteId = null,
    string? ClienteNombre = null,
    string? EmpresaNombre = null,
    DateOnly? FechaVencimientoDetectada = null,
    bool? TieneFirmaDetectada = null,
    DateOnly? FechaEmisionIntroducida = null,
    DateOnly? FechaVencimientoIntroducida = null,
    int? NumeroPaginas = null,
    Guid? AuditoriaExtraccionIaId = null,
    bool VigenciaLaFijaElTipo = false)
{
    /// <summary>Misma regla que <c>RevisionIaDocumento.TieneAlertaDeterminista</c>: la interfaz la refleja, el servidor la impone.</summary>
    public bool TieneAlertaDeterminista => MotivosRevisionIa.ContieneAlerta(Motivo);
}

/// <summary>Una revisión IA pendiente y el Documento al que pertenece (la fila de la cola de Revisión IA).</summary>
public sealed class RevisionIaPendienteConDocumento
{
    public required RevisionIaDocumento Revision { get; init; }
    public required Documento Documento { get; init; }
}

/// <summary>
/// La cola de Revisión IA: <b>la</b> definición de qué revisiones están pendientes y quién las alcanza. La lee la pestaña
/// Revisión IA (<see cref="ObtenerRevisionesIaPendientesQueryHandler"/>) y el filtro «Pendiente de revisión IA» del listado
/// de Documentos (<c>ObtenerDocumentosQuery.PendienteDeRevisionIa</c>): una sola consulta, para que las dos superficies no
/// puedan contar cosas distintas.
/// </summary>
public static class ColaRevisionIa
{
    /// <summary>
    /// Los roles que hoy acceden a la pestaña Revisión IA (gestión documental). Los mismos que autorizan esa pestaña en la
    /// pantalla de Documentos (<c>RolesDeGestionDocumental</c>); un test de la Web fija que no se separen. Solo decide si el
    /// filtro del listado se atiende: no autoriza ningún Command, que siguen con su propia barrera.
    /// </summary>
    public static readonly IReadOnlyList<string> RolesQueAccedenALaPestana =
        ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

    /// <summary>¿Accede el rol efectivo a la pestaña Revisión IA? Sin rol (Sesión Privilegiada, sin sesión), no: falla cerrado.</summary>
    public static bool AccedeElRol(string? rolEfectivo) =>
        rolEfectivo is not null && RolesQueAccedenALaPestana.Contains(rolEfectivo);

    /// <summary>
    /// Las revisiones sin resolver cuyo Documento alcanza quien mira. Los Documentos de Trabajador se acotan por cartera
    /// (<paramref name="trabajadorIdsVisibles"/>); los demás (Empresa…) no mapean al modelo de cartera, que es por Trabajador,
    /// y solo los ven los roles de visión total (<c>null</c>), el mismo criterio de atribución que
    /// <c>EnvioAlertasVencimientoHostedService</c>. Incluye revisiones de un Documento ya sustituido (análisis tardío de un
    /// histórico): el listado de Documentos, que solo enseña operativos, las estrecha después.
    /// </summary>
    public static IQueryable<RevisionIaPendienteConDocumento> Pendientes(
        IDocumentosQueryContext documentosContext, IReadOnlyList<Guid>? trabajadorIdsVisibles) =>
        from revision in documentosContext.RevisionesIaDocumento
        where !revision.Resuelta
        join documento in documentosContext.Documentos on revision.DocumentoId equals documento.Id
        where documento.TrabajadorId != null
            ? trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
            : trabajadorIdsVisibles == null
        select new RevisionIaPendienteConDocumento { Revision = revision, Documento = documento };

    /// <summary>Los Documentos con alguna revisión pendiente en la cola de quien mira.</summary>
    public static IQueryable<Guid> DocumentoIds(
        IDocumentosQueryContext documentosContext, IReadOnlyList<Guid>? trabajadorIdsVisibles) =>
        Pendientes(documentosContext, trabajadorIdsVisibles).Select(p => p.Revision.DocumentoId);
}

public class ObtenerRevisionesIaPendientesQueryHandler(
    IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
    IResolverClientePrincipalService resolverClientePrincipal,
    IAlcanceDatosService alcanceDatos,
    IDocumentosIaQueryContext documentosIaContext)
    : IRequestHandler<ObtenerRevisionesIaPendientesQuery, IReadOnlyList<RevisionIaDocumentoDto>>
{
    public async Task<IReadOnlyList<RevisionIaDocumentoDto>> Handle(
        ObtenerRevisionesIaPendientesQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        // Qué está pendiente y quién lo alcanza (cartera por Trabajador; Empresa solo para visión total) lo define
        // ColaRevisionIa, la misma consulta que usa el filtro «Pendiente de revisión IA» del listado de Documentos.
        var consulta =
            from pendiente in ColaRevisionIa.Pendientes(documentosContext, trabajadorIdsVisibles)
            let revision = pendiente.Revision
            let documento = pendiente.Documento
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join trabajadorJoin in trabajadoresContext.Trabajadores on documento.TrabajadorId equals trabajadorJoin.Id into trabajadores
            from trabajador in trabajadores.DefaultIfEmpty()
            join empresaJoin in empresasContext.Empresas on documento.EmpresaId equals empresaJoin.Id into empresas
            from empresa in empresas.DefaultIfEmpty()
            // Confianza ascendente (mockup Documentos TALVEG, pestaña IA): lo
            // menos fiable primero — es lo que más necesita un vistazo humano,
            // no lo más reciente.
            orderby revision.ConfianzaGeneral ascending, revision.CreadaEnUtc descending
            select new RevisionIaDocumentoDto(
                revision.Id, revision.DocumentoId,
                trabajador != null
                    ? trabajador.Nombre + " " + trabajador.Apellidos
                    : empresa != null ? empresa.RazonSocial : "—",
                tipoDocumento.Nombre,
                revision.ConfianzaGeneral, revision.TipoDetectado, revision.FechaEmisionDetectada, revision.Motivo, revision.CreadaEnUtc,
                documento.TrabajadorId,
                // Documento de Empresa: la Empresa ya es documento.EmpresaId. Documento
                // de Trabajador: la del propio Trabajador (documento.EmpresaId es null
                // ahí) — la sub-agrupación Empresa→Trabajador de "Requiere atención"
                // (GrupoCola, vocabulario Consultora) necesita esta última tanto como
                // la primera.
                documento.TrabajadorId != null ? trabajador!.EmpresaId : documento.EmpresaId,
                null,
                null,
                documento.TrabajadorId == null && empresa != null ? empresa.RazonSocial : null,
                revision.FechaVencimientoDetectada,
                revision.TieneFirmaDetectada,
                documento.FechaEmision,
                documento.FechaVencimiento,
                null,
                revision.AuditoriaExtraccionIaId,
                // Mismo predicado que TipoDocumento.FijaVigenciaDesdeLaEmision, escrito como expresión traducible.
                tipoDocumento.AplicaVencimientoAutomatico && tipoDocumento.VigenciaMeses != null);

        var revisiones = await consulta.ToListAsync(cancellationToken);
        if (revisiones.Count == 0) return revisiones;

        var trabajadorIds = revisiones.Where(r => r.TrabajadorId is not null).Select(r => r.TrabajadorId!.Value).Distinct().ToList();
        var clientePrincipalPorTrabajador = await resolverClientePrincipal.ResolverPorTrabajadorAsync(trabajadorIds, cancellationToken);

        var conCliente = revisiones
            .Select(r => r.TrabajadorId is { } trabajadorId && clientePrincipalPorTrabajador.TryGetValue(trabajadorId, out var principal)
                ? r with { ClienteId = principal.ClienteId, ClienteNombre = principal.ClienteNombre }
                : r)
            .ToList();

        // EmpresaNombre de las revisiones de Documento de Trabajador (la
        // "Documento de Empresa" ya lo trae de la consulta, vía el join a
        // Empresa que ya hacía falta para PropietarioNombre).
        var empresaIdsSinNombre = conCliente
            .Where(r => r.TrabajadorId is not null && r.EmpresaId is not null)
            .Select(r => r.EmpresaId!.Value)
            .Distinct()
            .ToList();
        var conNombresEmpresa = conCliente;
        if (empresaIdsSinNombre.Count > 0)
        {
            var nombresPorEmpresa = await empresasContext.Empresas
                .Where(e => empresaIdsSinNombre.Contains(e.Id))
                .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

            conNombresEmpresa = conCliente
                .Select(r => r.TrabajadorId is not null && r.EmpresaId is { } empresaId && nombresPorEmpresa.TryGetValue(empresaId, out var nombre)
                    ? r with { EmpresaNombre = nombre }
                    : r)
                .ToList();
        }

        var auditoriaIds = conNombresEmpresa
            .Where(r => r.AuditoriaExtraccionIaId is not null)
            .Select(r => r.AuditoriaExtraccionIaId!.Value)
            .Distinct()
            .ToList();
        var paginasPorAuditoria = await documentosIaContext.AuditoriasExtraccionIa
            .Where(a => auditoriaIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.NumeroPaginas, cancellationToken);

        return conNombresEmpresa
            .Select(r => r.AuditoriaExtraccionIaId is { } auditoriaId && paginasPorAuditoria.TryGetValue(auditoriaId, out var numeroPaginas)
                ? r with { NumeroPaginas = numeroPaginas }
                : r)
            .ToList();
    }
}

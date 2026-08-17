using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos;
using CaeManager.Application.Empresas;
using CaeManager.Application.TiposDocumento;
using CaeManager.Application.Trabajadores;
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
    string? EmpresaNombre = null);

public class ObtenerRevisionesIaPendientesQueryHandler(
    IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
    IResolverClientePrincipalService resolverClientePrincipal,
    IAlcanceDatosService alcanceDatos)
    : IRequestHandler<ObtenerRevisionesIaPendientesQuery, IReadOnlyList<RevisionIaDocumentoDto>>
{
    public async Task<IReadOnlyList<RevisionIaDocumentoDto>> Handle(
        ObtenerRevisionesIaPendientesQuery request, CancellationToken cancellationToken)
    {
        var trabajadorIdsVisibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);

        // Alcance: los Documentos de Trabajador se acotan por cartera, como
        // siempre. Los de Empresa no mapean al modelo de cartera (que es por
        // trabajador) — se muestran solo a los roles de visión total
        // (trabajadorIdsVisibles == null), el mismo criterio de atribución
        // que EnvioAlertasVencimientoHostedService.
        var consulta =
            from revision in documentosContext.RevisionesIaDocumento
            where !revision.Resuelta
            join documento in documentosContext.Documentos on revision.DocumentoId equals documento.Id
            join tipoDocumento in tiposDocumentoContext.TiposDocumento on documento.TipoDocumentoId equals tipoDocumento.Id
            join trabajadorJoin in trabajadoresContext.Trabajadores on documento.TrabajadorId equals trabajadorJoin.Id into trabajadores
            from trabajador in trabajadores.DefaultIfEmpty()
            join empresaJoin in empresasContext.Empresas on documento.EmpresaId equals empresaJoin.Id into empresas
            from empresa in empresas.DefaultIfEmpty()
            where documento.TrabajadorId != null
                ? trabajadorIdsVisibles == null || trabajadorIdsVisibles.Contains(documento.TrabajadorId!.Value)
                : trabajadorIdsVisibles == null
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
                documento.TrabajadorId == null && empresa != null ? empresa.RazonSocial : null);

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
        if (empresaIdsSinNombre.Count == 0) return conCliente;

        var nombresPorEmpresa = await empresasContext.Empresas
            .Where(e => empresaIdsSinNombre.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, e => e.RazonSocial, cancellationToken);

        return conCliente
            .Select(r => r.TrabajadorId is not null && r.EmpresaId is { } empresaId && nombresPorEmpresa.TryGetValue(empresaId, out var nombre)
                ? r with { EmpresaNombre = nombre }
                : r)
            .ToList();
    }
}

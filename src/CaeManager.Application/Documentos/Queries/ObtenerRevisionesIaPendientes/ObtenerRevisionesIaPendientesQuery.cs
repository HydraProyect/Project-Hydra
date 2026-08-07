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

/// <summary><paramref name="PropietarioNombre"/> es el trabajador ("Nombre Apellidos") o la razón social de la Empresa, según el ámbito del Documento.</summary>
public record RevisionIaDocumentoDto(
    Guid Id,
    Guid DocumentoId,
    string PropietarioNombre,
    string TipoDocumentoNombre,
    int ConfianzaGeneral,
    string? TipoDetectado,
    DateOnly? FechaEmisionDetectada,
    string Motivo,
    DateTime CreadaEnUtc);

public class ObtenerRevisionesIaPendientesQueryHandler(
    IDocumentosQueryContext documentosContext, ITiposDocumentoQueryContext tiposDocumentoContext,
    ITrabajadoresQueryContext trabajadoresContext, IEmpresasQueryContext empresasContext,
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
            orderby revision.CreadaEnUtc descending
            select new RevisionIaDocumentoDto(
                revision.Id, revision.DocumentoId,
                trabajador != null
                    ? trabajador.Nombre + " " + trabajador.Apellidos
                    : empresa != null ? empresa.RazonSocial : "—",
                tipoDocumento.Nombre,
                revision.ConfianzaGeneral, revision.TipoDetectado, revision.FechaEmisionDetectada, revision.Motivo, revision.CreadaEnUtc);

        return await consulta.ToListAsync(cancellationToken);
    }
}

using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.ObtenerValidacionOficialDocumento;

/// <summary>
/// El resultado de la validación oficial de un Documento (si existe): la
/// cabecera con nivel de confianza y decisión, y sus firmas verificadas.
/// Null cuando el documento nunca pasó por el circuito (su tipo no tiene
/// perfil oficial, o todavía no se procesó).
/// </summary>
public record ObtenerValidacionOficialDocumentoQuery(Guid DocumentoId) : IRequest<ValidacionOficialDocumentoDto?>;

public record ValidacionOficialDocumentoDto(
    PerfilDocumentoOficial Perfil,
    NivelConfianzaDocumental NivelConfianza,
    DecisionValidacionOficial Decision,
    ResultadoCotejoDocumentoOficial ResultadoCotejo,
    string Motivos,
    string? CodigoVerificacion,
    string? CifDetectado,
    string? RazonSocialDetectada,
    DateOnly? FechaEmisionDetectada,
    string? PeriodoDetectado,
    DateTime CreadaEnUtc,
    IReadOnlyList<FirmaDigitalDocumentoDto> Firmas);

public record FirmaDigitalDocumentoDto(
    int Indice,
    EstadoFirmaPdf Estado,
    bool CadenaConfiable,
    ComprobacionRevocacion Revocacion,
    string EmisorCertificado,
    bool EsSelloDeOrgano,
    string? FirmanteNombre,
    string? FirmanteNif,
    DateTime? FechaFirmaUtc,
    bool TieneSelloDeTiempo,
    bool CubreDocumentoCompleto,
    string? MotivoInvalidez);

public class ObtenerValidacionOficialDocumentoQueryHandler(IDocumentosQueryContext documentosContext)
    : IRequestHandler<ObtenerValidacionOficialDocumentoQuery, ValidacionOficialDocumentoDto?>
{
    public async Task<ValidacionOficialDocumentoDto?> Handle(
        ObtenerValidacionOficialDocumentoQuery request, CancellationToken cancellationToken)
    {
        var verificacion = await documentosContext.VerificacionesDocumentoOficial
            .FirstOrDefaultAsync(v => v.DocumentoId == request.DocumentoId, cancellationToken);
        if (verificacion is null)
            return null;

        var firmas = await documentosContext.FirmasDigitalesDocumento
            .Where(f => f.DocumentoId == request.DocumentoId)
            .OrderBy(f => f.Indice)
            .Select(f => new FirmaDigitalDocumentoDto(
                f.Indice, f.Estado, f.CadenaConfiable, f.Revocacion, f.EmisorCertificado, f.EsSelloDeOrgano,
                f.FirmanteNombre, f.FirmanteNif, f.FechaFirmaUtc, f.TieneSelloDeTiempo, f.CubreDocumentoCompleto,
                f.MotivoInvalidez))
            .ToListAsync(cancellationToken);

        return new ValidacionOficialDocumentoDto(
            verificacion.Perfil, verificacion.NivelConfianza, verificacion.Decision, verificacion.ResultadoCotejo,
            verificacion.Motivos, verificacion.CodigoVerificacion, verificacion.CifDetectado,
            verificacion.RazonSocialDetectada, verificacion.FechaEmisionDetectada, verificacion.PeriodoDetectado,
            verificacion.CreadaEnUtc, firmas);
    }
}

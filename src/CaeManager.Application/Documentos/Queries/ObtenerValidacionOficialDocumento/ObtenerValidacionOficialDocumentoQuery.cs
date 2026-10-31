using CaeManager.Application.Common;
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
    IReadOnlyList<FirmaDigitalDocumentoDto> Firmas,
    bool CodigoVerificacionDisponible);

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

/// <remarks>
/// En Sesión Privilegiada se proyecta sin <c>CodigoVerificacion</c>: <c>cae_app_soporte</c> no tiene <c>SELECT</c>
/// sobre esa columna (migración <c>SoporteSinCodigoVerificacionNiPayloadWebhook</c>; OWASP ASVS 4.0.3 V4.1.3), y
/// materializar la entidad daría 42501. <c>CodigoVerificacionDisponible</c> en <c>false</c> dice que no se ha
/// podido leer, distinto de un <c>CodigoVerificacion</c> nulo con la bandera en <c>true</c>, que dice que el
/// documento no lo trae. La señal es la misma coordenada en bruto con la que
/// <c>TenantRlsConnectionInterceptor</c> adopta el rol, como en <c>ObtenerConexionesIntegracionQueryHandler</c>.
/// </remarks>
public class ObtenerValidacionOficialDocumentoQueryHandler(
    IDocumentosQueryContext documentosContext, IClienteActivoSeleccionado clienteActivoSeleccionado)
    : IRequestHandler<ObtenerValidacionOficialDocumentoQuery, ValidacionOficialDocumentoDto?>
{
    public async Task<ValidacionOficialDocumentoDto?> Handle(
        ObtenerValidacionOficialDocumentoQuery request, CancellationToken cancellationToken)
    {
        ValidacionOficialDocumentoDto? verificacion;
        if (clienteActivoSeleccionado.SesionPrivilegiadaIdSeleccionada is not null)
        {
            verificacion = await documentosContext.VerificacionesDocumentoOficial
                .Where(v => v.DocumentoId == request.DocumentoId)
                .Select(v => new ValidacionOficialDocumentoDto(
                    v.Perfil, v.NivelConfianza, v.Decision, v.ResultadoCotejo, v.Motivos, null,
                    v.CifDetectado, v.RazonSocialDetectada, v.FechaEmisionDetectada, v.PeriodoDetectado,
                    v.CreadaEnUtc, Array.Empty<FirmaDigitalDocumentoDto>(), false))
                .FirstOrDefaultAsync(cancellationToken);
        }
        else
        {
            var entidad = await documentosContext.VerificacionesDocumentoOficial
                .FirstOrDefaultAsync(v => v.DocumentoId == request.DocumentoId, cancellationToken);
            verificacion = entidad is null ? null : new ValidacionOficialDocumentoDto(
                entidad.Perfil, entidad.NivelConfianza, entidad.Decision, entidad.ResultadoCotejo,
                entidad.Motivos, entidad.CodigoVerificacion, entidad.CifDetectado, entidad.RazonSocialDetectada,
                entidad.FechaEmisionDetectada, entidad.PeriodoDetectado, entidad.CreadaEnUtc, [], true);
        }
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

        return verificacion with { Firmas = firmas };
    }
}

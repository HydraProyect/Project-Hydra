using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.ContratosAcceso.Queries.ObtenerLecturaInbound;

// Las tres lecturas Inbound comparten autoridad (LecturaInboundAutoridad) antes de llamar al lector: la cartera no
// sustituye a la función de la base, ni la función a la cartera. Null = no encontrado, sin distinguir contrato invisible,
// rol que no valida o falta de autorización aplicable del Tenant de la Empresa contratista. Para que los Documentos no
// distingan «contrato visible sin autorización» (lista vacía) de «contrato invisible» (null), su handler pregunta antes
// por la autorización y responde null si no hay una vigente hoy; una lista vacía solo dice «autorizado, sin Documentos en
// el alcance» (o una revocación entre las dos llamadas, que la base corta igual).

/// <summary>¿Hay autorización del Tenant de la Empresa contratista para este contrato?</summary>
public record ObtenerAutorizacionInboundQuery(Guid ContratoAccesoId) : IRequest<AutorizacionInboundDto?>;

/// <summary>Los Documentos autorizados por este contrato, sin archivo.</summary>
public record ObtenerDocumentosInboundQuery(Guid ContratoAccesoId) : IRequest<IReadOnlyList<DocumentoInboundDto>?>;

/// <summary>
/// El contenido de un Documento original autorizado, en solo lectura (Inbound M1, PR 4): el binario y su tipo MIME, nunca la
/// referencia del almacenamiento del Tenant de la Empresa contratista. De un tipo de salud solo se abre el certificado de
/// «solo conclusión de aptitud», porque la función de la base no deja pasar ningún otro.
/// </summary>
public record ObtenerArchivoInboundQuery(Guid ContratoAccesoId, Guid DocumentoId) : IRequest<ContenidoArchivoInboundDto?>;

public record ContenidoArchivoInboundDto(byte[] Contenido, string TipoMime);

public class ObtenerAutorizacionInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser,
    ILectorDocumentosInbound lector)
    : IRequestHandler<ObtenerAutorizacionInboundQuery, AutorizacionInboundDto?>
{
    public async Task<AutorizacionInboundDto?> Handle(ObtenerAutorizacionInboundQuery request, CancellationToken cancellationToken) =>
        await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken)
            ? await lector.AutorizacionDelContratoAsync(request.ContratoAccesoId, cancellationToken)
            : null;
}

public class ObtenerDocumentosInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser,
    ILectorDocumentosInbound lector)
    : IRequestHandler<ObtenerDocumentosInboundQuery, IReadOnlyList<DocumentoInboundDto>?>
{
    public async Task<IReadOnlyList<DocumentoInboundDto>?> Handle(ObtenerDocumentosInboundQuery request, CancellationToken cancellationToken)
    {
        if (!await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken))
            return null;

        var autorizacion = await lector.AutorizacionDelContratoAsync(request.ContratoAccesoId, cancellationToken);
        return autorizacion is { VigenteHoy: true }
            ? await lector.DocumentosAutorizadosAsync(request.ContratoAccesoId, cancellationToken)
            : null;
    }
}

public class ObtenerArchivoInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser,
    ILectorDocumentosInbound lector, IAlmacenOriginalesInbound almacenOriginales)
    : IRequestHandler<ObtenerArchivoInboundQuery, ContenidoArchivoInboundDto?>
{
    public async Task<ContenidoArchivoInboundDto?> Handle(ObtenerArchivoInboundQuery request, CancellationToken cancellationToken)
    {
        if (!await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken))
            return null;

        var tenantContratista = await contratosContext.ContratosAcceso
            .Where(c => c.Id == request.ContratoAccesoId)
            .Select(c => c.TenantContratistaId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenantContratista is not { } tenantPropietario)
            return null;

        // La referencia sale de la función de la base, que ya comprobó la autorización y dejó la traza de la apertura; aun
        // así tiene que ser de la carpeta del Tenant del contrato, y no sale de aquí: solo el contenido y su tipo MIME.
        var archivo = await lector.ArchivoAutorizadoAsync(request.ContratoAccesoId, request.DocumentoId, cancellationToken);
        if (archivo?.ArchivoUrl is not { } referencia || !OriginalesInbound.EsDelTenant(referencia, tenantPropietario))
            return null;

        try
        {
            var contenido = await OriginalesInbound.LeerAsync(almacenOriginales, tenantPropietario, referencia, cancellationToken);
            return new ContenidoArchivoInboundDto(contenido, OriginalesInbound.TipoMime(referencia));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}

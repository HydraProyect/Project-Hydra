using CaeManager.Application.Common;
using CaeManager.Application.ContratosAcceso.LecturaInbound;
using MediatR;

namespace CaeManager.Application.ContratosAcceso.Queries.ObtenerLecturaInbound;

// Las tres lecturas Inbound comparten autoridad (LecturaInboundAutoridad) antes de llamar al lector: la cartera no
// sustituye a la función de la base, ni la función a la cartera. Null = no encontrado, sin distinguir contrato invisible,
// rol que no valida o falta de autorización del Tenant de la Empresa contratista.

/// <summary>¿Hay autorización del Tenant de la Empresa contratista para este contrato?</summary>
public record ObtenerAutorizacionInboundQuery(Guid ContratoAccesoId) : IRequest<AutorizacionInboundDto?>;

/// <summary>Los Documentos autorizados por este contrato, sin archivo.</summary>
public record ObtenerDocumentosInboundQuery(Guid ContratoAccesoId) : IRequest<IReadOnlyList<DocumentoInboundDto>?>;

/// <summary>
/// La referencia del archivo de un Documento autorizado. Servir el binario con <c>IFileStorageService</c> queda fuera de
/// este PR.
/// </summary>
public record ObtenerArchivoInboundQuery(Guid ContratoAccesoId, Guid DocumentoId) : IRequest<ArchivoInboundDto?>;

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
    public async Task<IReadOnlyList<DocumentoInboundDto>?> Handle(ObtenerDocumentosInboundQuery request, CancellationToken cancellationToken) =>
        await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken)
            ? await lector.DocumentosAutorizadosAsync(request.ContratoAccesoId, cancellationToken)
            : null;
}

public class ObtenerArchivoInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser,
    ILectorDocumentosInbound lector)
    : IRequestHandler<ObtenerArchivoInboundQuery, ArchivoInboundDto?>
{
    public async Task<ArchivoInboundDto?> Handle(ObtenerArchivoInboundQuery request, CancellationToken cancellationToken) =>
        await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken)
            ? await lector.ArchivoAutorizadoAsync(request.ContratoAccesoId, request.DocumentoId, cancellationToken)
            : null;
}

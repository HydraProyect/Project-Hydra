using CaeManager.Application.Inbound.Autorizacion;
using CaeManager.Application.Common;
using CaeManager.Application.Inbound.ContratosAcceso.EvidenciasInbound;
using CaeManager.Application.Inbound.ContratosAcceso.LecturaInbound;
using CaeManager.Application.Tenants.Encargo;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Inbound.ContratosAcceso.Queries.ObtenerLecturaInbound;

// Las lecturas Inbound comparten autoridad (LecturaInboundAutoridad) antes de llamar al lector: la cartera no
// sustituye a la función de la base, ni la función a la cartera. Null = no encontrado, sin distinguir contrato invisible,
// rol que no valida o falta de autorización aplicable del Tenant de la Empresa contratista. Para que los Documentos no
// distingan «contrato visible sin autorización» (lista vacía) de «contrato invisible» (null), su handler pregunta antes
// por la autorización y responde null si no hay una vigente hoy; una lista vacía solo dice «autorizado, sin Documentos en
// el alcance» (o una revocación entre las dos llamadas, que la base corta igual).

/// <summary>¿Hay autorización del Tenant de la Empresa contratista para este contrato?</summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerAutorizacionInboundQuery(Guid ContratoAccesoId) : IRequest<AutorizacionInboundDto?>;

/// <summary>Los Documentos autorizados por este contrato, sin archivo.</summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerDocumentosInboundQuery(Guid ContratoAccesoId) : IRequest<IReadOnlyList<DocumentoInboundDto>?>;

/// <summary>
/// El contenido de un Documento original autorizado, en solo lectura (Inbound M1, PR 4): el binario y su tipo MIME, nunca la
/// referencia del almacenamiento del Tenant de la Empresa contratista. De un tipo de salud solo se abre el certificado de
/// «solo conclusión de aptitud», porque la función de la base no deja pasar ningún otro.
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerArchivoInboundQuery(Guid ContratoAccesoId, Guid DocumentoId) : IRequest<ContenidoArchivoInboundDto?>;

public record ContenidoArchivoInboundDto(byte[] Contenido, string TipoMime);

public class ObtenerAutorizacionInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser,
    ILectorDocumentosInbound lector, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerAutorizacionInboundQuery, AutorizacionInboundDto?>
{
    public async Task<AutorizacionInboundDto?> Handle(ObtenerAutorizacionInboundQuery request, CancellationToken cancellationToken) =>
        await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken)
        && await LecturaInboundAutoridad.PuedeLeerAsync(request.ContratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken)
            ? await lector.AutorizacionDelContratoAsync(request.ContratoAccesoId, cancellationToken)
            : null;
}

public class ObtenerDocumentosInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser,
    ILectorDocumentosInbound lector, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerDocumentosInboundQuery, IReadOnlyList<DocumentoInboundDto>?>
{
    public async Task<IReadOnlyList<DocumentoInboundDto>?> Handle(ObtenerDocumentosInboundQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken))
            return null;

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
    ILectorDocumentosInbound lector, IAlmacenOriginalesInbound almacenOriginales, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerArchivoInboundQuery, ContenidoArchivoInboundDto?>
{
    public async Task<ContenidoArchivoInboundDto?> Handle(ObtenerArchivoInboundQuery request, CancellationToken cancellationToken)
    {
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken))
            return null;

        return await AbrirTrasLaCapacidadAsync(
            request.ContratoAccesoId, request.DocumentoId, contratosContext, alcanceDatos, currentUser, lector, almacenOriginales,
            cancellationToken);
    }

    /// <summary>
    /// La apertura una vez pedida la capacidad: cartera (<see cref="LecturaInboundAutoridad"/>), función de la base (que
    /// comprueba la autorización y deja la traza <c>Apertura</c>), carpeta del Tenant del contrato y lectura del original.
    /// La comparte <see cref="ObtenerArchivoParaValidarInboundQueryHandler"/>, que pide además <c>ValidarInbound</c>.
    /// </summary>
    internal static async Task<ContenidoArchivoInboundDto?> AbrirTrasLaCapacidadAsync(
        Guid contratoAccesoId, Guid documentoId, IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos,
        ICurrentUserService currentUser, ILectorDocumentosInbound lector, IAlmacenOriginalesInbound almacenOriginales,
        CancellationToken cancellationToken)
    {
        if (!await LecturaInboundAutoridad.PuedeLeerAsync(contratoAccesoId, contratosContext, alcanceDatos, currentUser, cancellationToken))
            return null;

        var tenantContratista = await contratosContext.ContratosAcceso
            .Where(c => c.Id == contratoAccesoId)
            .Select(c => c.TenantContratistaId)
            .FirstOrDefaultAsync(cancellationToken);
        if (tenantContratista is not { } tenantPropietario)
            return null;

        // La referencia sale de la función de la base, que ya comprobó la autorización y dejó la traza de la apertura; aun
        // así tiene que ser de la carpeta del Tenant del contrato, y no sale de aquí: solo el contenido y su tipo MIME.
        var archivo = await lector.ArchivoAutorizadoAsync(contratoAccesoId, documentoId, cancellationToken);
        if (archivo is null || !OriginalesInbound.EsDelTenantDelContrato(archivo, tenantPropietario))
            return null;

        try
        {
            var contenido = await OriginalesInbound.LeerAsync(almacenOriginales, archivo, cancellationToken);
            return new ContenidoArchivoInboundDto(contenido, OriginalesInbound.TipoMime(archivo.ArchivoUrl!));
        }
        catch (FileNotFoundException)
        {
            return null;
        }
    }
}

/// <summary>
/// El archivo de un Documento original autorizado, pedido desde la cola de validación (demo Inbound P6): el Gestor CAE del
/// Tenant TCE lo abre para validarlo o rechazarlo. Misma apertura que <see cref="ObtenerArchivoInboundQuery"/> (cartera,
/// función de la base con su traza <c>Apertura</c> en el Tenant de la Empresa contratista, carpeta del Tenant del contrato),
/// y además exige <c>ValidarInbound</c>, la capacidad de la cola: sin ella o sin <c>LeerInbound</c>, null, igual que un
/// Documento no autorizado, revocado, sustituido, de un tipo de salud sin marca o de un contrato de otro Tenant. De un tipo
/// de salud solo se abre el certificado de «solo conclusión de aptitud» (DN-5 (b)), porque la función de la base no deja
/// pasar ningún otro.
/// </summary>
[AreaDelEncargo("ContratosAcceso")]
public record ObtenerArchivoParaValidarInboundQuery(Guid ContratoAccesoId, Guid DocumentoId) : IRequest<ContenidoArchivoInboundDto?>;

public class ObtenerArchivoParaValidarInboundQueryHandler(
    IContratosAccesoQueryContext contratosContext, IAlcanceDatosService alcanceDatos, ICurrentUserService currentUser,
    ILectorDocumentosInbound lector, IAlmacenOriginalesInbound almacenOriginales, IAutorizacionInbound autorizacionInbound)
    : IRequestHandler<ObtenerArchivoParaValidarInboundQuery, ContenidoArchivoInboundDto?>
{
    public async Task<ContenidoArchivoInboundDto?> Handle(ObtenerArchivoParaValidarInboundQuery request, CancellationToken cancellationToken)
    {
        // Las dos capacidades, antes de tocar nada: la de la cola y la de la lectura que deja la traza.
        if (!await autorizacionInbound.TieneAsync(CapacidadInbound.ValidarInbound, cancellationToken)
            || !await autorizacionInbound.TieneAsync(CapacidadInbound.LeerInbound, cancellationToken))
            return null;

        return await ObtenerArchivoInboundQueryHandler.AbrirTrasLaCapacidadAsync(
            request.ContratoAccesoId, request.DocumentoId, contratosContext, alcanceDatos, currentUser, lector, almacenOriginales,
            cancellationToken);
    }
}

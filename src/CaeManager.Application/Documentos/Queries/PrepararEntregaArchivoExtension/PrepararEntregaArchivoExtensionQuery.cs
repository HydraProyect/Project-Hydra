using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Commands.ConfirmarSubidaDesdeExtension;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionSubida;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Proyectos;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Queries.PrepararEntregaArchivoExtension;

/// <summary>
/// ¿Puede salir ahora el archivo de esta Acreditación hacia el formulario de esta plataforma CAE de terceros? Es la
/// autoridad del servidor antes de entregar el archivo a la extensión de navegador: la pregunta se hace al emitir el
/// ticket y otra vez al servir el contenido, y la respuesta no sale de lo que la extensión diga haber comprobado.
///
/// <para>
/// Exige, en este orden: que la Acreditación sea visible para quien pide (<see cref="AcreditacionSubible"/>: Tenant por
/// RLS, Centro en su Asignación de Cartera y Documento en su alcance), que siga pendiente de subir por un canal de
/// plataforma de un Centro con gestión CAE y con su Documento operativo (lo mismo que la lista de la extensión), que el
/// Proveedor del canal esté activo (kill switch) y que <see cref="DominioPortal"/> sea uno de sus dominios. Lo que no es
/// visible o ya no está pendiente sale como «no encontrada», sin decir cuál de las dos.
/// </para>
///
/// <para>
/// El dominio lo declara el cliente: la comprobación protege frente a una extensión desactualizada o con un error, NO
/// frente a un cliente hostil con un token válido. Lo que acota a ese cliente son la cartera, el estado pendiente, el
/// límite de ritmo y el registro de acceso.
/// </para>
/// </summary>
public record PrepararEntregaArchivoExtensionQuery(Guid AcreditacionId, string? DominioPortal) : IRequest<Result<EntregaArchivoExtensionDto>>;

/// <param name="DocumentoId">El Documento de la Acreditación: lo que se servirá, no lo que el cliente pida.</param>
/// <param name="HostPortal">El host ya normalizado, que queda fijado en el ticket de entrega.</param>
public record EntregaArchivoExtensionDto(Guid DocumentoId, string HostPortal);

public class PrepararEntregaArchivoExtensionQueryHandler(
    IAcreditacionDocumentoPlataformaRepository acreditacionRepositorio, IDocumentoRepository documentoRepositorio,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext, ICentrosQueryContext centrosContext,
    IProveedoresPlataformaCaeQueryContext proveedoresContext, IDocumentosQueryContext documentosContext)
    : IRequestHandler<PrepararEntregaArchivoExtensionQuery, Result<EntregaArchivoExtensionDto>>
{
    public async Task<Result<EntregaArchivoExtensionDto>> Handle(PrepararEntregaArchivoExtensionQuery request, CancellationToken cancellationToken)
    {
        var acreditacion = await AcreditacionSubible.CargarVisibleAsync(
            request.AcreditacionId, acreditacionRepositorio, documentoRepositorio, alcanceDatos, proyectosContext, centrosContext,
            recargar: false, cancellationToken);
        if (acreditacion is null || !await SigueEntregableAsync(acreditacion, cancellationToken))
            return Result.Fallo<EntregaArchivoExtensionDto>(AcreditacionSubible.NoEncontrada);

        var canalId = acreditacion.CanalGestionDocumentalId;
        if (!await PortalDelCanalAcreditacion.ProveedorActivoAsync(canalId, centrosContext, proveedoresContext, cancellationToken))
            return Result.Fallo<EntregaArchivoExtensionDto>(Error.Crear(
                ConfirmarSubidaDesdeExtensionCommandHandler.CodigoConectorInactivo, "Este conector está desactivado temporalmente."));

        var host = ResolucionProveedorPlataformaCaeService.ExtraerHost(request.DominioPortal);
        if (host is null)
            return Result.Fallo<EntregaArchivoExtensionDto>(Error.Crear(
                ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalNoDeclarado,
                "La extensión no indicó en qué portal va el documento. Actualiza la extensión e inténtalo de nuevo."));

        if (!await PortalDelCanalAcreditacion.HostEsDelProveedorAsync(canalId, host, centrosContext, proveedoresContext, cancellationToken))
            return Result.Fallo<EntregaArchivoExtensionDto>(Error.Crear(
                ConfirmarSubidaDesdeExtensionCommandHandler.CodigoPortalAjeno, await MensajePortalAjenoAsync(canalId, cancellationToken)));

        return Result.Exito(new EntregaArchivoExtensionDto(acreditacion.DocumentoId, host));
    }

    /// <summary>
    /// Lo que <c>ObtenerAcreditacionesPorProveedorQuery</c> pide a una fila de la lista de la extensión. Solo vale
    /// <see cref="EstadoAcreditacion.PendienteDeSubir"/>: una Acreditación Subida o Aceptada ya no necesita el archivo y
    /// una Rechazada exige antes una versión nueva, que la deja de nuevo pendiente.
    /// </summary>
    private async Task<bool> SigueEntregableAsync(AcreditacionDocumentoPlataforma acreditacion, CancellationToken cancellationToken)
    {
        if (acreditacion.Estado != EstadoAcreditacion.PendienteDeSubir)
            return false;

        var canalVigente = await (
            from canal in centrosContext.CanalesGestionDocumental
            where canal.Id == acreditacion.CanalGestionDocumentalId && canal.Tipo == TipoCanalGestion.Plataforma
            join centro in centrosContext.Centros on canal.CentroId equals centro.Id
            where centro.GestionCae != ModalidadGestionCae.SinGestionCae
            select canal.Id).AnyAsync(cancellationToken);

        return canalVigente && await documentosContext.Documentos.Operativos()
            .AnyAsync(d => d.Id == acreditacion.DocumentoId, cancellationToken);
    }

    private async Task<string> MensajePortalAjenoAsync(Guid canalGestionDocumentalId, CancellationToken cancellationToken)
    {
        var nombre = await PortalDelCanalAcreditacion.NombreProveedorAsync(
            canalGestionDocumentalId, centrosContext, proveedoresContext, cancellationToken);

        return nombre is null
            ? "Este documento es de otra Plataforma CAE: no se puede poner en este portal."
            : $"Este documento es de otra Plataforma CAE: debe subirse en la Plataforma CAE {nombre}, no se puede poner en este portal.";
    }
}

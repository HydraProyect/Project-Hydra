using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Queries.PrepararDivisionAdjunto;

/// <summary>
/// Sugiere cómo dividir un PDF recibido, sin escribir nada salvo la fila de acceso: abre el adjunto y sigue por
/// <see cref="PrepararDivisionPdfQuery"/>, que detecta los tramos solo en local.
///
/// No hay comando que la cubra con <c>AutorizacionEscrituraBehavior</c>, así que la puerta es esta: el reparto es trabajo
/// interno del Tenant, y el catálogo que devuelve lleva Trabajadores y DNI, de modo que el rol Cliente de portal no pasa
/// (<see cref="IPoliticaNotaInterna"/>, como <c>ObtenerRepartoAdjuntosQuery</c>). El resto lo decide
/// <see cref="DivisionDeAdjuntos"/>. Como en la descarga, sin fila de acceso no se abre el archivo.
/// </summary>
public record PrepararDivisionAdjuntoQuery(Guid AdjuntoId, Guid EmpresaGestionadaId) : IRequest<Result<PreparacionDivisionPdf>>;

public class PrepararDivisionAdjuntoQueryHandler(
    IDisponibilidadHerramientasPdf disponibilidad, IComunicacionesQueryContext comunicacionesContext,
    IAlcanceDatosService alcanceDatos, ICurrentUserService currentUserService, IPoliticaNotaInterna politicaNotaInterna,
    IRegistroAccesoDatoSensibleService registroAcceso, IFileStorageService almacen, IMediator mediator)
    : IRequestHandler<PrepararDivisionAdjuntoQuery, Result<PreparacionDivisionPdf>>
{
    public async Task<Result<PreparacionDivisionPdf>> Handle(PrepararDivisionAdjuntoQuery request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Result.Fallo<PreparacionDivisionPdf>(Error.Crear(PrepararDivisionPdfQueryHandler.ErrorNoDisponible, "Herramientas PDF no está disponible."));

        if (!await politicaNotaInterna.PuedeLeerAsync(cancellationToken))
            return Result.Fallo<PreparacionDivisionPdf>(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));

        var origen = await RepartoAdjuntos.ObtenerAsync(comunicacionesContext, request.AdjuntoId, cancellationToken);
        if (await DivisionDeAdjuntos.MotivoParaNoDividirAsync(origen, request.EmpresaGestionadaId, alcanceDatos, currentUserService, cancellationToken) is { } motivo)
            return Result.Fallo<PreparacionDivisionPdf>(motivo);

        await registroAcceso.RegistrarAsync(nameof(AdjuntoMensaje), request.AdjuntoId, cancellationToken);

        var contenido = await DivisionDeAdjuntos.LeerPdfAsync(almacen, origen!.ArchivoUrl, origen.NombreArchivo, cancellationToken);
        if (contenido is null)
            return Result.Fallo<PreparacionDivisionPdf>(DivisionDeAdjuntos.ArchivoNoValido());

        return await mediator.Send(new PrepararDivisionPdfQuery(contenido, request.EmpresaGestionadaId), cancellationToken);
    }
}

using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Common;
using CaeManager.Domain.Integraciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.PedirPrioridadValidacion;

/// <summary>
/// Fase G: envía el borrador construido por
/// <see cref="Queries.ObtenerBorradorPedirPrioridad.ObtenerBorradorPedirPrioridadQuery"/>
/// (ya editado por el Gestor) y deja rastro en
/// <see cref="SolicitudPrioridadDocumento"/>. La conexión de correo se
/// resuelve aquí, en el servidor — nunca se confía en un
/// <c>ConexionIntegracionId</c> que llegara del cliente — con la misma
/// lógica que ya usó la Query para sugerir el borrador.
/// </summary>
public record PedirPrioridadValidacionCommand(
    Guid CentroId, IReadOnlyList<string> Destinatarios, string Asunto, string CuerpoHtml) : ICommand;

public class PedirPrioridadValidacionCommandValidator : AbstractValidator<PedirPrioridadValidacionCommand>
{
    public PedirPrioridadValidacionCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.Destinatarios).NotEmpty().WithMessage("Indica al menos un destinatario.");
        RuleForEach(c => c.Destinatarios).EmailAddress().WithMessage("Alguno de los destinatarios no es un correo válido.");
        RuleFor(c => c.Asunto).NotEmpty().WithMessage("El asunto es obligatorio.").MaximumLength(Conversacion.LongitudMaximaAsunto);
        RuleFor(c => c.CuerpoHtml).NotEmpty().WithMessage("El mensaje no puede estar vacío.");
    }
}

public class PedirPrioridadValidacionCommandHandler(
    ICentrosQueryContext centrosContext,
    IIntegracionesQueryContext integracionesContext,
    ISolicitudPrioridadDocumentoRepository solicitudRepositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IMediator mediator,
    IUnitOfWork unitOfWork)
    : IRequestHandler<PedirPrioridadValidacionCommand, Result>
{
    public async Task<Result> Handle(PedirPrioridadValidacionCommand request, CancellationToken cancellationToken)
    {
        var centro = await centrosContext.Centros.FirstOrDefaultAsync(c => c.Id == request.CentroId, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("SolicitudPrioridad.SinUsuario", "No pudimos identificar quién envía esta solicitud."));

        var conexionId = await integracionesContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado == EstadoConexionIntegracion.Habilitada)
            .Where(c => c.ClienteId == null || c.ClienteId == centro.ClienteId)
            .OrderByDescending(c => c.ClienteId != null)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (conexionId is null)
            return Result.Fallo(Error.Crear(
                "SolicitudPrioridad.SinBuzonConectado", "No hay ningún buzón de correo conectado — conecta uno de Microsoft 365 antes de enviar."));

        var envioResultado = await mediator.Send(
            new EnviarMensajeNuevoCommand(conexionId.Value, request.Destinatarios, request.Asunto, request.CuerpoHtml, centro.ClienteId),
            cancellationToken);
        if (envioResultado.EsFallido)
            return Result.Fallo(envioResultado.Error);

        solicitudRepositorio.Agregar(new SolicitudPrioridadDocumento(centro.Id, usuarioId.Value, DateTime.UtcNow));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

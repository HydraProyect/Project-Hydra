using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.ActualizarDocumentoDesdeAdjunto;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Comunicaciones.Commands.DividirAdjuntoPdf;

/// <param name="EmpresaGestionadaId">La misma que en Dividir: la del catálogo con el que se revisaron los tramos.</param>
/// <param name="Tramos">Revisados por una persona, vengan o no de <c>PrepararDivisionAdjuntoQuery</c>.</param>
public record DividirAdjuntoPdfCommand(Guid AdjuntoId, Guid EmpresaGestionadaId, IReadOnlyList<TramoACrear> Tramos)
    : ICommand<IReadOnlyList<ResultadoTramoCreado>>;

public class DividirAdjuntoPdfCommandValidator : AbstractValidator<DividirAdjuntoPdfCommand>
{
    public DividirAdjuntoPdfCommandValidator()
    {
        RuleFor(c => c.AdjuntoId).NotEmpty();
        RuleFor(c => c.EmpresaGestionadaId).NotEmpty();
        RuleFor(c => c.Tramos).NotEmpty().WithMessage("No hay documentos que crear.");
    }
}

/// <summary>
/// Divide un PDF recibido en una conversación. <b>No envía nada</b>: los Documentos salen de
/// <see cref="CrearDocumentosDesdeDivisionCommand"/>, que decide titulares, tipos, páginas y el alta de cada tramo. Aquí
/// se añade lo que ese comando no puede ver: el alcance sobre el adjunto (ver <see cref="DivisionDeAdjuntos"/>) y su estado.
/// El rol lo decide <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>.
///
/// Los Documentos quedan en el Tenant propietario del adjunto: su <c>TenantId</c> viaja como Tenant esperado, y cada alta
/// lo sella con el Tenant actual, que tiene que coincidir.
///
/// El adjunto se reserva como <see cref="EstadoRepartoAdjunto.Dividido"/> antes de crear nada. No puede guardarse con los
/// Documentos, como la asignación, porque cada tramo confirma por separado: reservado antes, el token <c>Version</c> deja
/// pasar una sola división y otra sesión recibe <see cref="ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto"/>
/// sin haber creado nada. Si no consta ningún Documento, ni confirmado ni en duda, vuelve a pendiente. Si alguno consta,
/// se queda dividido aunque otros tramos se rechacen, porque repetir duplicaría los creados: el resultado dice qué pasó
/// con cada tramo y el archivo sigue en el adjunto.
///
/// Desde la reserva no se atiende la cancelación: a medias no se sabría qué tramos llegaron a crearse.
/// </summary>
public class DividirAdjuntoPdfCommandHandler(
    IDisponibilidadHerramientasPdf disponibilidad, IComunicacionesQueryContext comunicacionesContext,
    IAdjuntoMensajeRepository repositorio, IEventoConversacionRepository eventoRepositorio, IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService, IRegistroAccesoDatoSensibleService registroAcceso, IFileStorageService almacen,
    IMediator mediator, IUnitOfWork unitOfWork,
    ILogger<DividirAdjuntoPdfCommandHandler> logger)
    : IRequestHandler<DividirAdjuntoPdfCommand, Result<IReadOnlyList<ResultadoTramoCreado>>>
{
    public async Task<Result<IReadOnlyList<ResultadoTramoCreado>>> Handle(DividirAdjuntoPdfCommand request, CancellationToken cancellationToken)
    {
        if (!await disponibilidad.EstaDisponibleAsync(cancellationToken))
            return Fallo(Error.Crear(PrepararDivisionPdfQueryHandler.ErrorNoDisponible, "Herramientas PDF no está disponible."));

        var origen = await RepartoAdjuntos.ObtenerAsync(comunicacionesContext, request.AdjuntoId, cancellationToken);
        if (await DivisionDeAdjuntos.MotivoParaNoDividirAsync(origen, request.EmpresaGestionadaId, alcanceDatos, currentUserService, cancellationToken) is { } motivo)
            return Fallo(motivo);

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } usuarioId)
            return Fallo(Error.Crear("Adjunto.SinAutor", "No pudimos identificar quién divide el adjunto."));

        // Esta lectura fija el token con el que se reserva.
        var adjunto = await repositorio.ObtenerPorIdAsync(request.AdjuntoId, cancellationToken);
        if (adjunto is null)
            return Fallo(Error.Crear("Adjunto.NoEncontrado", "No encontramos este adjunto."));
        if (!adjunto.EstaPendiente)
            return Fallo(Error.Crear("Adjunto.YaResuelto", "Este adjunto ya está resuelto."));

        // Como en la consulta: sin fila de acceso no se abre el archivo, llegue o no por ella.
        await registroAcceso.RegistrarAsync(nameof(AdjuntoMensaje), adjunto.Id, cancellationToken);

        var contenido = await DivisionDeAdjuntos.LeerPdfAsync(almacen, adjunto.ArchivoUrl, adjunto.NombreArchivo, cancellationToken);
        if (contenido is null)
            return Fallo(DivisionDeAdjuntos.ArchivoNoValido());

        if (await ReservarAsync(adjunto, usuarioId, cancellationToken) is { } conflicto)
            return Fallo(conflicto);

        Result<IReadOnlyList<ResultadoTramoCreado>> lote;
        try
        {
            lote = await mediator.Send(
                new CrearDocumentosDesdeDivisionCommand(contenido, adjunto.NombreArchivo, adjunto.TenantId, request.EmpresaGestionadaId, request.Tramos),
                CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Cada tramo atrapa sus propios fallos salvo la cancelación: lo demás ocurrió antes del primero, sin Documentos.
            // Con una cancelación no se sabe, y dividido sin Documentos es mejor que pendiente con ellos.
            await DeshacerReservaAsync(adjunto);
            throw;
        }

        if (lote.EsFallido || !lote.Valor.Any(ConstaDocumento))
        {
            await DeshacerReservaAsync(adjunto);
            return lote;
        }

        await RegistrarEnTimelineAsync(origen!.ConversacionId, adjunto.Id);

        return lote;
    }

    private static Result<IReadOnlyList<ResultadoTramoCreado>> Fallo(Error error) =>
        Result.Fallo<IReadOnlyList<ResultadoTramoCreado>>(error);

    // Un alta sin confirmar pudo guardarse: cuenta como Documento, porque deshacer permitiría repetirla.
    private static bool ConstaDocumento(ResultadoTramoCreado tramo) =>
        tramo.Estado is EstadoTramoCreado.Creado or EstadoTramoCreado.SinConfirmar;

    private async Task<Error?> ReservarAsync(AdjuntoMensaje adjunto, Guid usuarioId, CancellationToken cancellationToken)
    {
        adjunto.MarcarDividido(usuarioId, DateTime.UtcNow);
        try
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (DbUpdateConcurrencyException)
        {
            repositorio.OlvidarCambios(adjunto);
            return Error.Crear(
                ActualizarDocumentoDesdeAdjuntoCommandHandler.CodigoConflicto,
                "Este adjunto cambió mientras se dividía, y no se ha creado ningún documento. Revísalo y vuelve a intentarlo.");
        }
        catch (Exception)
        {
            repositorio.OlvidarCambios(adjunto);
            throw;
        }
    }

    private async Task DeshacerReservaAsync(AdjuntoMensaje adjunto)
    {
        try
        {
            adjunto.DeshacerDivision();
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            // No debe tapar el resultado del lote. El adjunto queda dividido sin Documentos: no duplica nada y su archivo sigue ahí.
            repositorio.OlvidarCambios(adjunto);
            logger.LogError("No se pudo devolver a pendiente el adjunto {AdjuntoId} tras una división sin Documentos ({TipoExcepcion}).",
                adjunto.Id, ex.GetType().Name);
        }
    }

    // Tras los commits y a mejor esfuerzo, como el evento de la asignación: un fallo aquí no puede perder la lista de lo creado.
    // Sin una entrada por Documento: esas se ven en el portal con el nombre del titular, y aquí pueden ser Trabajadores de
    // la cartera de la Empresa gestionada, ajenos al hilo.
    private async Task RegistrarEnTimelineAsync(Guid conversacionId, Guid adjuntoId)
    {
        try
        {
            eventoRepositorio.Agregar(new EventoConversacion(conversacionId, TipoEventoConversacion.AdjuntoDividido, adjuntoId, DateTime.UtcNow));
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogWarning("No se pudo registrar en el timeline la división del adjunto {AdjuntoId} ({TipoExcepcion}).",
                adjuntoId, ex.GetType().Name);
        }
    }
}

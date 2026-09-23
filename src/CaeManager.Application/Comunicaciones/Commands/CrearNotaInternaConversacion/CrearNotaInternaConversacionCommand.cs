using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.CrearNotaInternaConversacion;

public record CrearNotaInternaConversacionCommand(Guid ConversacionId, string Texto) : ICommand<Guid>;

public class CrearNotaInternaConversacionCommandValidator : AbstractValidator<CrearNotaInternaConversacionCommand>
{
    public CrearNotaInternaConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La nota interna debe pertenecer a una conversación.");
        RuleFor(c => c.Texto).NotEmpty().WithMessage("La nota interna no puede estar vacía.");
        RuleFor(c => c.Texto)
            .Must(t => t is null || t.Trim().Length <= NotaInternaConversacion.LongitudMaximaTexto)
            .WithMessage($"La nota interna no puede superar {NotaInternaConversacion.LongitudMaximaTexto} caracteres.");
    }
}

/// <summary>
/// Añade una nota interna del equipo al Unified Timeline de una conversación.
/// <b>No envía nada</b>: no depende de Graph, de WhatsApp ni de ningún canal —
/// la nota se queda en el Tenant propietario.
///
/// Quién puede escribirla no se decide aquí: lo decide
/// <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/> para todo
/// <see cref="ICommandBase"/> (Administrador, DireccionCae, CoordinadorCae y
/// GestorCae; ninguna sesión privilegiada de plataforma, porque ninguna tiene
/// camino de escritura fuera del aprovisionamiento). Este handler añade el
/// escalón de <b>alcance</b>: solo sobre un hilo que el usuario puede ver, con el
/// mismo criterio que responderlo (<see cref="AlcanceDatosServiceExtensions.ConversacionVisibleAsync"/>).
/// </summary>
public class CrearNotaInternaConversacionCommandHandler(
    IComunicacionesQueryContext comunicacionesContext,
    INotaInternaConversacionRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CrearNotaInternaConversacionCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearNotaInternaConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await comunicacionesContext.Conversaciones
            .Where(c => c.Id == request.ConversacionId)
            .Select(c => new { c.ClienteId, c.EmpresaId, c.ConexionIntegracionId })
            .FirstOrDefaultAsync(cancellationToken);

        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(
                conversacion.ClienteId, conversacion.EmpresaId, conversacion.ConexionIntegracionId, cancellationToken))
            return Result.Fallo<Guid>(Error.Crear("Conversacion.NoEncontrada", "No encontramos esta conversación."));

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } autorId)
            return Result.Fallo<Guid>(Error.Crear("NotaInterna.SinAutor", "No pudimos identificar al autor de la nota."));

        var nota = new NotaInternaConversacion(request.ConversacionId, autorId, request.Texto, DateTime.UtcNow);
        repositorio.Agregar(nota);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(nota.Id);
    }
}

using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Comunicaciones.Commands.ResolverRespuestaSugerida;

public enum AccionRespuestaSugerida
{
    Insertar = 0,
    Confirmar = 1,
    Descartar = 2
}

public record ResolverRespuestaSugeridaCommand(Guid RespuestaSugeridaId, AccionRespuestaSugerida Accion) : ICommand;

public class ResolverRespuestaSugeridaCommandValidator : AbstractValidator<ResolverRespuestaSugeridaCommand>
{
    public ResolverRespuestaSugeridaCommandValidator()
    {
        RuleFor(c => c.RespuestaSugeridaId).NotEmpty().WithMessage("Indica la respuesta sugerida.");
        RuleFor(c => c.Accion).IsInEnum().WithMessage("Esa acción no existe.");
    }
}

/// <summary>
/// Una persona inserta, confirma o descarta la respuesta sugerida. Solo escribe el estado: ni edita el texto ni envía nada.
/// Quién puede lo decide <see cref="AutorizacionEscrituraBehavior{TRequest,TResponse}"/>; aquí va el alcance de la
/// conversación dueña, con la misma respuesta que si la sugerencia no existiera.
/// </summary>
public class ResolverRespuestaSugeridaCommandHandler(
    IComunicacionesQueryContext comunicaciones,
    IRespuestaSugeridaRepository respuestas,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ResolverRespuestaSugeridaCommand, Result>
{
    public async Task<Result> Handle(ResolverRespuestaSugeridaCommand request, CancellationToken cancellationToken)
    {
        var noEncontrada = Result.Fallo(Error.Crear("Sugerencia.NoEncontrada", "No encontramos esta respuesta sugerida."));

        var respuesta = await respuestas.ObtenerPorIdAsync(request.RespuestaSugeridaId, cancellationToken);
        if (respuesta is null)
            return noEncontrada;

        var conversacion = await comunicaciones.Conversaciones.FirstOrDefaultAsync(c => c.Id == respuesta.ConversacionId, cancellationToken);
        if (conversacion is null || !await alcanceDatos.ConversacionVisibleAsync(conversacion, cancellationToken))
            return noEncontrada;

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } autorId)
            return Result.Fallo(Error.Crear("Sugerencia.SinAutor", "No pudimos identificar quién resuelve la sugerencia."));

        var resultado = request.Accion switch
        {
            AccionRespuestaSugerida.Insertar => respuesta.Insertar(),
            AccionRespuestaSugerida.Confirmar => respuesta.Confirmar(autorId, DateTime.UtcNow),
            AccionRespuestaSugerida.Descartar => respuesta.Descartar(),
            _ => Result.Fallo(RespuestaSugerida.ErrorTransicionNoPermitida)
        };
        if (resultado.EsFallido)
            return resultado;

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

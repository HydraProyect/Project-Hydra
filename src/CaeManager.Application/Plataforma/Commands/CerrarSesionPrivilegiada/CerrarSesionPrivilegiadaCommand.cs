using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Plataforma.Commands.CerrarSesionPrivilegiada;

/// <summary>
/// Cierra una sesión privilegiada antes de que venza su ventana.
///
/// Va en el mismo incremento que la apertura y no en uno posterior: una
/// ceremonia que permite abrir y obliga a esperar a que caduque no es media
/// funcionalidad, es un incentivo a abrir ventanas cortas "por si acaso" y a
/// dejarlas correr. La ventana acota el peor caso; cerrar es lo que hace que el
/// caso normal dure lo que dura la incidencia.
///
/// No hace falta comprobar de quién es la sesión: la política de RLS (F2b-5)
/// solo devuelve las sesiones que cuelgan de una concesión que nombra al usuario
/// actual, así que una sesión ajena no se encuentra aunque se pida por Id. Se
/// deja dicho aquí porque la ausencia de una comprobación explícita es
/// justamente lo que un revisor querrá entender.
/// </summary>
public record CerrarSesionPrivilegiadaCommand(Guid SesionPrivilegiadaId) : ICommand;

public class CerrarSesionPrivilegiadaCommandValidator : AbstractValidator<CerrarSesionPrivilegiadaCommand>
{
    public CerrarSesionPrivilegiadaCommandValidator()
    {
        RuleFor(c => c.SesionPrivilegiadaId).NotEmpty();
    }
}

public class CerrarSesionPrivilegiadaCommandHandler(
    IPlataformaQueryContext plataformaContext,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CerrarSesionPrivilegiadaCommand, Result>
{
    public async Task<Result> Handle(CerrarSesionPrivilegiadaCommand request, CancellationToken cancellationToken)
    {
        var sesion = await plataformaContext.SesionesPrivilegiadas
            .FirstOrDefaultAsync(s => s.Id == request.SesionPrivilegiadaId, cancellationToken);

        if (sesion is null)
            return Result.Fallo(Error.Crear(
                "SesionPrivilegiada.NoEncontrada", "No encontramos esa sesión de soporte."));

        if (!sesion.EstaAbierta)
            return Result.Fallo(Error.Crear(
                "SesionPrivilegiada.YaCerrada", "Esa sesión de soporte ya estaba cerrada."));

        sesion.Cerrar(DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

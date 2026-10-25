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
/// Solo puede cerrar el Actor de Plataforma TALVEG nombrado en la concesión.
/// El Administrador del Tenant beneficiario puede leer la Sesión Privilegiada,
/// pero esa visibilidad no autoriza su cierre. RLS mantiene la barrera de escritura;
/// si no actualiza la fila, el comando devuelve un error de dominio.
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
    IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<CerrarSesionPrivilegiadaCommand, Result>
{
    public async Task<Result> Handle(CerrarSesionPrivilegiadaCommand request, CancellationToken cancellationToken)
    {
        var sesion = await plataformaContext.SesionesPrivilegiadas
            .FirstOrDefaultAsync(s => s.Id == request.SesionPrivilegiadaId, cancellationToken);

        if (sesion is null)
            return Result.Fallo(Error.Crear(
                "SesionPrivilegiada.NoEncontrada", "No encontramos esa sesión de soporte."));

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null || !await plataformaContext.ConcesionesPrivilegio.AnyAsync(
                c => c.Id == sesion.ConcesionPrivilegioId && c.UsuarioPlataformaId == usuarioId,
                cancellationToken))
            return CierreNoPermitido();

        if (!sesion.EstaAbierta)
            return Result.Fallo(Error.Crear(
                "SesionPrivilegiada.YaCerrada", "Esa sesión de soporte ya estaba cerrada."));

        sesion.Cerrar(DateTime.UtcNow);
        try
        {
            if (await unitOfWork.SaveChangesAsync(cancellationToken) == 0)
                return CierreNoPermitido();
        }
        catch (DbUpdateConcurrencyException)
        {
            return CierreNoPermitido();
        }

        return Result.Exito();
    }

    private static Result CierreNoPermitido() => Result.Fallo(Error.Crear(
        "SesionPrivilegiada.CierreNoPermitido",
        "No se pudo cerrar la sesión de soporte. Solo puede cerrarla el Actor de Plataforma TALVEG que la abrió; puede que ya haya cambiado."));
}

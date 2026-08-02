using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.EliminarTrabajador;

public record EliminarTrabajadorCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarTrabajadorCommandHandler(ITrabajadorRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarTrabajadorCommand, Result>
{
    public async Task<Result> Handle(EliminarTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));

        trabajador.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

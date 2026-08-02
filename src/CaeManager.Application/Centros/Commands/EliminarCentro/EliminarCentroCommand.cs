using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EliminarCentro;

public record EliminarCentroCommand(Guid Id, Guid UsuarioId) : ICommand;

public class EliminarCentroCommandHandler(ICentroRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarCentroCommand, Result>
{
    public async Task<Result> Handle(EliminarCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        centro.MarcarComoEliminado(request.UsuarioId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

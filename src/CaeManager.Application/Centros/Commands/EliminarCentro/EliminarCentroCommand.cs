using CaeManager.Application.Asignaciones;
using CaeManager.Application.Common;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EliminarCentro;

public record EliminarCentroCommand(Guid Id) : ICommand;

public class EliminarCentroCommandHandler(
    ICentroRepository repositorio,
    IAsignacionRepository asignaciones,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork,
    ICurrentUserService currentUserService)
    : IRequestHandler<EliminarCentroCommand, Result>
{
    public async Task<Result> Handle(EliminarCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        // La identidad se resuelve aquí, no se recibe del comando (auditoría
        // Módulo 5, hallazgo crítico 7/9): un UsuarioId de contrato público es
        // auditoría falsificable — cualquier llamador, presente o futuro, puede
        // pasar el GUID que quiera. Sin sesión resuelta, se aborta: nunca se
        // atribuye el borrado a Guid.Empty.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Centro.SinIdentidad", "No se pudo confirmar tu identidad. Vuelve a iniciar sesión e inténtalo de nuevo."));

        centro.MarcarComoEliminado(usuarioId.Value);
        await CierreDeAsignaciones.PorCentroEliminadoAsync(asignaciones, centro.Id, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

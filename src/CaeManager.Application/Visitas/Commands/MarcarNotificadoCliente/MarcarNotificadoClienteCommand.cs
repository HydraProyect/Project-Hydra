using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.MarcarNotificadoCliente;

/// <summary>
/// Cambia el check "Notificado al cliente" independientemente del resto del
/// formulario — se marca directamente desde el listado, sin abrir el
/// drawer de edición, para no interrumpir el flujo de repasar varias
/// visitas seguidas.
/// </summary>
public record MarcarNotificadoClienteCommand(Guid Id, bool Notificado) : IRequest<Result>;

public class MarcarNotificadoClienteCommandHandler(IVisitaRepository repositorio, IUnitOfWork unitOfWork)
    : IRequestHandler<MarcarNotificadoClienteCommand, Result>
{
    public async Task<Result> Handle(MarcarNotificadoClienteCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null)
            return Result.Fallo(Error.Crear("Visita.NoEncontrada", "No encontramos esta visita."));

        visita.MarcarNotificadoCliente(request.Notificado);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

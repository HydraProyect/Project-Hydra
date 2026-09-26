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
public record MarcarNotificadoClienteCommand(Guid Id, bool Notificado) : ICommand;

public class MarcarNotificadoClienteCommandHandler(IVisitaRepository repositorio, IUnitOfWork unitOfWork, IAlcanceDatosService alcanceDatos)
    : IRequestHandler<MarcarNotificadoClienteCommand, Result>
{
    public async Task<Result> Handle(MarcarNotificadoClienteCommand request, CancellationToken cancellationToken)
    {
        // Alcance de GESTIÓN sobre el Centro, como al editar o cancelar: antes solo
        // se comprobaba que existiera en el Tenant, y un Gestor CAE podía marcar
        // Visitas fuera de su Asignación de Cartera conociendo su id.
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null || !await AutorizacionCancelacionVisita.PuedeGestionarAsync(alcanceDatos, visita, cancellationToken))
            return Result.Fallo(AutorizacionCancelacionVisita.NoEncontrada);

        // FS-11: una Visita cancelada no se modifica; primero se reactiva.
        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.Cancelada", "Esta visita está cancelada. Reactívala antes de modificarla."));

        visita.MarcarNotificadoCliente(request.Notificado);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

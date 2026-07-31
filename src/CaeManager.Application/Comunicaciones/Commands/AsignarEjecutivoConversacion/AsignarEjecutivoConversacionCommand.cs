using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Comunicaciones;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Comunicaciones.Commands.AsignarEjecutivoConversacion;

public record AsignarEjecutivoConversacionCommand(Guid ConversacionId, Guid? EjecutivoId) : IRequest<Result>;

public class AsignarEjecutivoConversacionCommandValidator : AbstractValidator<AsignarEjecutivoConversacionCommand>
{
    public AsignarEjecutivoConversacionCommandValidator()
    {
        RuleFor(c => c.ConversacionId).NotEmpty().WithMessage("La asignación debe indicar una conversación.");
    }
}

/// <summary>
/// El Ejecutivo se revalida contra <see cref="IDirectorioUsuariosService"/>.
/// Antes no se comprobaba, con el argumento de que "Web ya valida que el id
/// viene de un selector": un selector no es una frontera de autorización —
/// nada impide enviar otro Guid— y así se podía escribir cualquier valor en
/// <c>EjecutivoAsignadoId</c>, incluido el de un usuario de otro tenant
/// (hallazgo N-10 de INFORME-AUDITORIA-2.md).
///
/// Hace falta la abstracción porque <c>ApplicationUser</c> vive en
/// Infrastructure.Identity y Application no puede referenciarlo (mismo motivo
/// documentado en ReasignarEjecutivoClienteCommand, que tiene el mismo hueco
/// abierto).
/// </summary>
public class AsignarEjecutivoConversacionCommandHandler(
    IConversacionCorreoRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IDirectorioUsuariosService directorioUsuarios,
    IUnitOfWork unitOfWork)
    : IRequestHandler<AsignarEjecutivoConversacionCommand, Result>
{
    public async Task<Result> Handle(AsignarEjecutivoConversacionCommand request, CancellationToken cancellationToken)
    {
        var conversacion = await repositorio.ObtenerPorIdAsync(request.ConversacionId, cancellationToken);
        // El repositorio ya filtra por tenant; esto cierra el alcance dentro
        // del tenant (hallazgo N-3): un gestor con el Guid de una conversación
        // que ya no está en su cartera podía reasignarla.
        if (conversacion is null || !await alcanceDatos.ClienteOpcionalVisibleAsync(conversacion.ClienteId, cancellationToken))
            return Result.Fallo(Error.Crear("ConversacionCorreo.NoEncontrada", "No encontramos esta conversación."));

        // null es legítimo: es "desasignar".
        if (request.EjecutivoId is { } ejecutivoId &&
            !await directorioUsuarios.EsVisibleEnTenantActualAsync(ejecutivoId, cancellationToken))
            return Result.Fallo(Error.Crear(
                "AsignarEjecutivoConversacion.EjecutivoNoValido", "No encontramos a ese usuario en tu organización."));

        conversacion.Asignar(request.EjecutivoId);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

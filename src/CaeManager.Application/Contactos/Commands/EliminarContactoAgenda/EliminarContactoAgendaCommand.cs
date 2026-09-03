using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Contactos.Commands.EliminarContactoAgenda;

/// <summary>
/// Retira un contacto de la agenda. Se pasa también el propietario y se
/// comprueba que el contacto le pertenece: sin eso, conocer el Guid de un
/// contacto bastaría para borrarlo desde la agenda de otra entidad.
/// </summary>
public record EliminarContactoAgendaCommand(TipoPropietarioAgenda Tipo, Guid PropietarioId, Guid ContactoId) : ICommand;

public class EliminarContactoAgendaCommandValidator : AbstractValidator<EliminarContactoAgendaCommand>
{
    public EliminarContactoAgendaCommandValidator()
    {
        RuleFor(c => c.PropietarioId).NotEmpty();
        RuleFor(c => c.ContactoId).NotEmpty();
    }
}

public class EliminarContactoAgendaCommandHandler(
    IContactoAgendaRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EliminarContactoAgendaCommand, Result>
{
    public async Task<Result> Handle(EliminarContactoAgendaCommand request, CancellationToken cancellationToken)
    {
        var visible = request.Tipo switch
        {
            TipoPropietarioAgenda.Cliente => await alcanceDatos.ClienteVisibleAsync(request.PropietarioId, cancellationToken),
            // Defensa en profundidad (REC-149): inalcanzable para el rol
            // Cliente vía AutorizacionEscrituraBehavior; alcance de gestión
            // como segunda barrera independiente.
            TipoPropietarioAgenda.Empresa => await alcanceDatos.EmpresaParaGestionVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Subcontrata => await alcanceDatos.SubcontrataVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Centro => await alcanceDatos.CentroVisibleAsync(request.PropietarioId, cancellationToken),
            _ => false
        };

        if (!visible)
            return Result.Fallo(Error.Crear("ContactoAgenda.SinAcceso", "No encontramos esta entidad."));

        var contacto = await repositorio.ObtenerPorIdAsync(request.ContactoId, cancellationToken);

        var perteneceAlPropietario = contacto is not null && request.Tipo switch
        {
            TipoPropietarioAgenda.Cliente => contacto.ClienteId == request.PropietarioId,
            TipoPropietarioAgenda.Empresa => contacto.EmpresaId == request.PropietarioId,
            TipoPropietarioAgenda.Subcontrata => contacto.SubcontrataId == request.PropietarioId,
            TipoPropietarioAgenda.Centro => contacto.CentroId == request.PropietarioId,
            _ => false
        };

        if (!perteneceAlPropietario)
            return Result.Fallo(Error.Crear("ContactoAgenda.NoEncontrado", "No encontramos este contacto."));

        repositorio.Eliminar(contacto!);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

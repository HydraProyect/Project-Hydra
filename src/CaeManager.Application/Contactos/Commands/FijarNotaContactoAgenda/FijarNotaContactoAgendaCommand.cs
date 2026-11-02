using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Contactos.Commands.FijarNotaContactoAgenda;

/// <summary>
/// Fija la nota del contacto desde el panel de la conversación, sin reenviar el formulario entero de la Agenda. Texto
/// vacío la retira. Lleva el propietario por lo mismo que <c>EliminarContactoAgendaCommand</c>: conocer el Guid de un
/// contacto no basta para escribir en la agenda de otra entidad.
/// </summary>
public record FijarNotaContactoAgendaCommand(TipoPropietarioAgenda Tipo, Guid PropietarioId, Guid ContactoId, string? Texto) : ICommand;

public class FijarNotaContactoAgendaCommandValidator : AbstractValidator<FijarNotaContactoAgendaCommand>
{
    public FijarNotaContactoAgendaCommandValidator()
    {
        RuleFor(c => c.PropietarioId).NotEmpty();
        RuleFor(c => c.ContactoId).NotEmpty();
        RuleFor(c => c.Texto).MaximumLength(ContactoAgenda.LongitudMaximaNotas);
    }
}

public class FijarNotaContactoAgendaCommandHandler(
    IContactoAgendaRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<FijarNotaContactoAgendaCommand, Result>
{
    public async Task<Result> Handle(FijarNotaContactoAgendaCommand request, CancellationToken cancellationToken)
    {
        // Mismo alcance por rama que guardar o eliminar un contacto de la agenda.
        var visible = request.Tipo switch
        {
            TipoPropietarioAgenda.Cliente => await alcanceDatos.ClienteVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Empresa => await alcanceDatos.EmpresaParaGestionVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Subcontrata => await alcanceDatos.SubcontrataVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Centro => await alcanceDatos.CentroParaGestionVisibleAsync(request.PropietarioId, cancellationToken),
            _ => false
        };

        if (!visible)
            return Result.Fallo(Error.Crear("ContactoAgenda.SinAcceso", "No encontramos esta entidad."));

        var contacto = await repositorio.ObtenerPorIdAsync(request.ContactoId, cancellationToken);
        if (contacto is null || !GuardarContactoAgendaCommandHandler.PerteneceA(contacto, request.Tipo, request.PropietarioId))
            return Result.Fallo(Error.Crear("ContactoAgenda.NoEncontrado", "No encontramos este contacto."));

        if (await currentUserService.ObtenerUsuarioActualIdAsync() is not { } autorId)
            return Result.Fallo(Error.Crear("ContactoAgenda.SinAutor", "No pudimos identificar al autor de la nota."));

        contacto.FijarNotas(request.Texto, autorId, DateTime.UtcNow);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

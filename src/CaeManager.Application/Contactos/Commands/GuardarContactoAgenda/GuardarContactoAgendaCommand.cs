using CaeManager.Application.Common;
using CaeManager.Application.Contactos.Queries.ObtenerAgendaContactos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Contactos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Contactos.Commands.GuardarContactoAgenda;

/// <summary>
/// Alta y edición en el mismo Command: la pantalla es el mismo formulario y
/// la diferencia es un Id nulo o no. <paramref name="TipoDocumentoIds"/> llega
/// siempre completo (el conjunto entero de casillas marcadas), nunca como
/// "añade este" — ver <c>ContactoAgenda.EstablecerTiposDocumento</c>.
/// </summary>
public record GuardarContactoAgendaCommand(
    TipoPropietarioAgenda Tipo,
    Guid PropietarioId,
    Guid? ContactoId,
    string Nombre,
    string Email,
    string? Telefono,
    string? Cargo,
    string? Notas,
    bool EsPredeterminado,
    bool RecibeProgramacionVisitas,
    bool RecibeFacturacion,
    IReadOnlyList<Guid> TipoDocumentoIds) : ICommand<Guid>;

public class GuardarContactoAgendaCommandValidator : AbstractValidator<GuardarContactoAgendaCommand>
{
    public GuardarContactoAgendaCommandValidator()
    {
        RuleFor(c => c.PropietarioId).NotEmpty().WithMessage("El contacto debe pertenecer a una entidad.");
        RuleFor(c => c.Nombre).NotEmpty().WithMessage("El nombre es obligatorio.").MaximumLength(ContactoAgenda.LongitudMaximaNombre);
        RuleFor(c => c.Email)
            .NotEmpty().WithMessage("El email es obligatorio — la solicitud se envía siempre por correo.")
            .EmailAddress().WithMessage("El email no es válido.")
            .MaximumLength(ContactoAgenda.LongitudMaximaEmail);
        RuleFor(c => c.Telefono).MaximumLength(ContactoAgenda.LongitudMaximaTelefono);
        RuleFor(c => c.Cargo).MaximumLength(ContactoAgenda.LongitudMaximaCargo);
        RuleFor(c => c.Notas).MaximumLength(ContactoAgenda.LongitudMaximaNotas);
    }
}

public class GuardarContactoAgendaCommandHandler(
    IContactoAgendaRepository repositorio,
    TiposDocumento.ITiposDocumentoQueryContext tiposDocumentoContext,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarContactoAgendaCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(GuardarContactoAgendaCommand request, CancellationToken cancellationToken)
    {
        var visible = request.Tipo switch
        {
            TipoPropietarioAgenda.Cliente => await alcanceDatos.ClienteVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Empresa => await alcanceDatos.EmpresaVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Subcontrata => await alcanceDatos.SubcontrataVisibleAsync(request.PropietarioId, cancellationToken),
            TipoPropietarioAgenda.Centro => await alcanceDatos.CentroVisibleAsync(request.PropietarioId, cancellationToken),
            _ => false
        };

        if (!visible)
            return Result.Fallo<Guid>(Error.Crear("ContactoAgenda.SinAcceso", "No encontramos esta entidad."));

        // Los TipoDocumento llegan de casillas de la UI, así que se recargan y
        // revalidan aquí: un Id que no exista (o de otro tenant, invisible por
        // el filtro global) se descarta en vez de escribirse.
        var tipoDocumentoIdsValidos = request.TipoDocumentoIds.Count == 0
            ? []
            : await tiposDocumentoContext.TiposDocumento
                .Where(t => request.TipoDocumentoIds.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(cancellationToken);

        ContactoAgenda contacto;

        if (request.ContactoId is { } contactoId)
        {
            var existente = await repositorio.ObtenerPorIdAsync(contactoId, cancellationToken);
            if (existente is null || !PerteneceA(existente, request))
                return Result.Fallo<Guid>(Error.Crear("ContactoAgenda.NoEncontrado", "No encontramos este contacto."));

            existente.Actualizar(
                request.Nombre, request.Email, request.Telefono, request.Cargo, request.Notas,
                request.EsPredeterminado, request.RecibeProgramacionVisitas, request.RecibeFacturacion);
            contacto = existente;
        }
        else
        {
            contacto = request.Tipo switch
            {
                TipoPropietarioAgenda.Cliente => ContactoAgenda.DeCliente(
                    request.PropietarioId, request.Nombre, request.Email, request.Telefono, request.Cargo, request.Notas,
                    request.EsPredeterminado, request.RecibeProgramacionVisitas, request.RecibeFacturacion),
                TipoPropietarioAgenda.Empresa => ContactoAgenda.DeEmpresa(
                    request.PropietarioId, request.Nombre, request.Email, request.Telefono, request.Cargo, request.Notas,
                    request.EsPredeterminado, request.RecibeProgramacionVisitas, request.RecibeFacturacion),
                TipoPropietarioAgenda.Subcontrata => ContactoAgenda.DeSubcontrata(
                    request.PropietarioId, request.Nombre, request.Email, request.Telefono, request.Cargo, request.Notas,
                    request.EsPredeterminado, request.RecibeProgramacionVisitas, request.RecibeFacturacion),
                _ => ContactoAgenda.DeCentro(
                    request.PropietarioId, request.Nombre, request.Email, request.Telefono, request.Cargo, request.Notas,
                    request.EsPredeterminado, request.RecibeProgramacionVisitas, request.RecibeFacturacion)
            };

            repositorio.Agregar(contacto);
        }

        contacto.EstablecerTiposDocumento(tipoDocumentoIdsValidos);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito(contacto.Id);
    }

    private static bool PerteneceA(ContactoAgenda contacto, GuardarContactoAgendaCommand request) => request.Tipo switch
    {
        TipoPropietarioAgenda.Cliente => contacto.ClienteId == request.PropietarioId,
        TipoPropietarioAgenda.Empresa => contacto.EmpresaId == request.PropietarioId,
        TipoPropietarioAgenda.Subcontrata => contacto.SubcontrataId == request.PropietarioId,
        TipoPropietarioAgenda.Centro => contacto.CentroId == request.PropietarioId,
        _ => false
    };
}

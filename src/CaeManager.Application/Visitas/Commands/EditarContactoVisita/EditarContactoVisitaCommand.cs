using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Visitas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Visitas.Commands.EditarContactoVisita;

/// <summary>
/// Cambia el contacto de la Visita desde Visita 360 (decisión de producto, 2026-10-16). Es un ICommand: el
/// pipeline lo deniega a Consulta. Mismo alcance que <c>EditarVisitaCommand</c>: solo una Visita cuyo Centro está
/// dentro del alcance de GESTIÓN de quien la edita; fuera de alcance, y en otro Tenant, responde igual que si no
/// existiera, antes de comprobar la versión. No toca el contacto del Centro.
/// </summary>
public record EditarContactoVisitaCommand(Guid Id, Guid Version, string? Nombre, string? Telefono, string? Correo) : ICommand;

public class EditarContactoVisitaCommandValidator : AbstractValidator<EditarContactoVisitaCommand>
{
    public EditarContactoVisitaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.Nombre).MaximumLength(Visita.LongitudMaximaContactoNombre);
        RuleFor(c => c.Telefono).MaximumLength(Visita.LongitudMaximaContactoTelefono);
        RuleFor(c => c.Correo).MaximumLength(Visita.LongitudMaximaContactoCorreo);
        RuleFor(c => c.Correo).Cascade(CascadeMode.Stop)
            .EmailAddress().WithMessage("Introduce un correo electrónico válido.")
            .Must(c => Visita.EsCorreoDeContactoValido(c!.Trim())).WithMessage("Introduce un correo electrónico válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.Correo));
    }
}

public class EditarContactoVisitaCommandHandler(IVisitaRepository repositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarContactoVisitaCommand, Result>
{
    public async Task<Result> Handle(EditarContactoVisitaCommand request, CancellationToken cancellationToken)
    {
        var visita = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (visita is null || !await alcanceDatos.CentroParaGestionVisibleAsync(visita.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("Visita.NoEncontrada", "No encontramos esta visita."));

        // FS-11: una Visita cancelada no se modifica; primero se reactiva.
        if (visita.EstaCancelada)
            return Result.Fallo(Error.Crear("Visita.Cancelada", "Esta visita está cancelada. Reactívala antes de modificarla."));

        if (ConcurrenciaOptimista.Verificar(visita, request.Version, "esta visita") is { } conflicto)
            return Result.Fallo(conflicto);

        visita.ActualizarContacto(request.Nombre, request.Telefono, request.Correo);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

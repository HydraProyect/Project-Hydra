using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EditarFichaCentro;

public record EditarFichaCentroCommand(Guid Id, Guid Version,
    string? HorarioAcceso,
    string? ControlAccesosNombre,
    string? ControlAccesosTelefono,
    string? TecnicoPrevencionNombre,
    string? TecnicoPrevencionTelefono,
    string? TecnicoPrevencionCorreo,
    bool? TrabajosEnAltura,
    bool? EspaciosConfinados,
    bool? AtmosferasExplosivas) : ICommand;

public class EditarFichaCentroCommandValidator : AbstractValidator<EditarFichaCentroCommand>
{
    public EditarFichaCentroCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.HorarioAcceso).MaximumLength(500);
        RuleFor(c => c.ControlAccesosNombre).MaximumLength(200);
        RuleFor(c => c.ControlAccesosTelefono).MaximumLength(50);
        RuleFor(c => c.TecnicoPrevencionNombre).MaximumLength(200);
        RuleFor(c => c.TecnicoPrevencionTelefono).MaximumLength(50);
        RuleFor(c => c.TecnicoPrevencionCorreo).MaximumLength(254);
        RuleFor(c => c.TecnicoPrevencionCorreo).EmailAddress().WithMessage("Introduce un correo electrónico válido.")
            .When(c => !string.IsNullOrWhiteSpace(c.TecnicoPrevencionCorreo));
    }
}

public class EditarFichaCentroCommandHandler(ICentroRepository repositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarFichaCentroCommand, Result>
{
    public async Task<Result> Handle(EditarFichaCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroParaGestionVisibleAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));
        if (ConcurrenciaOptimista.Verificar(centro, request.Version, "este centro") is { } conflicto)
            return Result.Fallo(conflicto);
        centro.ActualizarFicha(
            request.HorarioAcceso,
            request.ControlAccesosNombre,
            request.ControlAccesosTelefono,
            request.TecnicoPrevencionNombre,
            request.TecnicoPrevencionTelefono,
            request.TecnicoPrevencionCorreo,
            request.TrabajosEnAltura,
            request.EspaciosConfinados,
            request.AtmosferasExplosivas);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

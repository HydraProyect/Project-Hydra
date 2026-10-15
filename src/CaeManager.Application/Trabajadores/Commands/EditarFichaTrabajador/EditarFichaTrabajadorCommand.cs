using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.EditarFichaTrabajador;

public record EditarFichaTrabajadorCommand(Guid Id, Guid Version,
    string? NumeroAfiliacionSeguridadSocial, string? Puesto, DateOnly? FechaNacimiento,
    string? Nacionalidad, string? TipoVinculo, DateOnly? FechaAltaEmpresa, DateOnly? FechaBajaEmpresa,
    string? FiguraEnRnt, string? TipoPermisoTrabajo, DateOnly? CaducidadPermisoTrabajo,
    string? MotivoPermisoTrabajo) : ICommand;

public class EditarFichaTrabajadorCommandValidator : AbstractValidator<EditarFichaTrabajadorCommand>
{
    public EditarFichaTrabajadorCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.NumeroAfiliacionSeguridadSocial).Must(Trabajador.NumeroAfiliacionValido)
            .WithMessage("Introduce un número de afiliación de 11 o 12 dígitos.");
        RuleFor(c => c.Puesto).MaximumLength(Trabajador.LongitudMaximaPuesto);
        RuleFor(c => c.Nacionalidad).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.TipoVinculo).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.FiguraEnRnt).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.TipoPermisoTrabajo).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.MotivoPermisoTrabajo).MaximumLength(Trabajador.LongitudMaximaCampoFicha);
        RuleFor(c => c.FechaBajaEmpresa)
            .Must((c, baja) => !baja.HasValue || !c.FechaAltaEmpresa.HasValue || baja >= c.FechaAltaEmpresa)
            .WithMessage("La baja no puede ser anterior al alta en la empresa.");
    }
}

public class EditarFichaTrabajadorCommandHandler(ITrabajadorRepository repositorio,
    IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarFichaTrabajadorCommand, Result>
{
    public async Task<Result> Handle(EditarFichaTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));
        if (trabajador.EstaAnonimizado)
            return Result.Fallo(Error.Crear("Trabajador.Anonimizado",
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse."));
        if (ConcurrenciaOptimista.Verificar(trabajador, request.Version, "este trabajador") is { } conflicto)
            return Result.Fallo(conflicto);

        trabajador.ActualizarFicha(request.NumeroAfiliacionSeguridadSocial, request.Puesto,
            request.FechaNacimiento, request.Nacionalidad, request.TipoVinculo, request.FechaAltaEmpresa,
            request.FechaBajaEmpresa, request.FiguraEnRnt, request.TipoPermisoTrabajo,
            request.CaducidadPermisoTrabajo, request.MotivoPermisoTrabajo);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

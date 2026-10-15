using CaeManager.Application.Common;
using CaeManager.Application.Trabajadores;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Vehiculos.Commands.EditarFichaVehiculo;

public record EditarFichaVehiculoCommand(Guid Id, Guid Version, string? Tipo, string? Titularidad,
    string? RevisionGrua, string? DeclaracionCe, string? Aseguradora, string? Poliza, string? Recibo,
    IReadOnlyList<Guid> TrabajadorIds) : ICommand;

public class EditarFichaVehiculoCommandValidator : AbstractValidator<EditarFichaVehiculoCommand>
{
    public EditarFichaVehiculoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.Version).NotEmpty();
        RuleFor(c => c.Tipo).MaximumLength(Vehiculo.LongitudMaximaCampoFicha);
        RuleFor(c => c.Titularidad).MaximumLength(Vehiculo.LongitudMaximaCampoFicha);
        RuleFor(c => c.RevisionGrua).MaximumLength(Vehiculo.LongitudMaximaCampoFicha);
        RuleFor(c => c.DeclaracionCe).MaximumLength(Vehiculo.LongitudMaximaCampoFicha);
        RuleFor(c => c.Aseguradora).MaximumLength(Vehiculo.LongitudMaximaCampoFicha);
        RuleFor(c => c.Poliza).MaximumLength(Vehiculo.LongitudMaximaCampoFicha);
        RuleFor(c => c.Recibo).MaximumLength(Vehiculo.LongitudMaximaCampoFicha);
        RuleFor(c => c.TrabajadorIds).NotNull();
        RuleForEach(c => c.TrabajadorIds).NotEmpty();
        RuleFor(c => c.TrabajadorIds).Must(ids => ids is null || ids.Distinct().Count() == ids.Count)
            .WithMessage("No se puede seleccionar dos veces al mismo trabajador.");
    }
}

public class EditarFichaVehiculoCommandHandler(IVehiculoRepository repositorio,
    ITrabajadoresQueryContext trabajadores, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarFichaVehiculoCommand, Result>
{
    public async Task<Result> Handle(EditarFichaVehiculoCommand request, CancellationToken cancellationToken)
    {
        var vehiculo = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (vehiculo is null || !await alcanceDatos.VehiculoVisibleAsync(request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Vehiculo.NoEncontrado", "No encontramos este vehículo."));
        if (ConcurrenciaOptimista.Verificar(vehiculo, request.Version, "este vehículo") is { } conflicto)
            return Result.Fallo(conflicto);

        var visibles = await alcanceDatos.ObtenerTrabajadorIdsVisiblesAsync(cancellationToken);
        var seleccionables = await trabajadores.Trabajadores
            .Where(t => t.TenantId == vehiculo.TenantId && !t.EstaEliminado
                && (visibles == null || visibles.Contains(t.Id)))
            .Select(t => t.Id).ToListAsync(cancellationToken);
        var seleccion = request.TrabajadorIds.ToHashSet();
        if (seleccion.Except(seleccionables).Any())
            return Result.Fallo(Error.Crear("Vehiculo.TrabajadorNoEncontrado", "No encontramos alguno de los trabajadores seleccionados."));

        vehiculo.ActualizarFicha(request.Tipo, request.Titularidad, request.RevisionGrua,
            request.DeclaracionCe, request.Aseguradora, request.Poliza, request.Recibo);

        // Solo se retira lo que el usuario puede desmarcar. Las relaciones con Trabajadores
        // fuera de su cartera o eliminados se conservan aunque no aparezcan en el selector.
        foreach (var conductor in vehiculo.Conductores
            .Where(c => seleccionables.Contains(c.TrabajadorId) && !seleccion.Contains(c.TrabajadorId)).ToList())
        {
            vehiculo.RetirarConductor(conductor);
            repositorio.QuitarConductor(conductor);
        }
        foreach (var trabajadorId in seleccion) vehiculo.AutorizarConductor(trabajadorId);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }
}

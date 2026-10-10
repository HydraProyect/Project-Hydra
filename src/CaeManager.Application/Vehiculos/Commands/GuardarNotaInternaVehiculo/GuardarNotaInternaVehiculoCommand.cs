using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Vehiculos;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Vehiculos.Commands.GuardarNotaInternaVehiculo;

/// <summary>
/// Guarda la «Nota interna» de la ficha Vehículo 360 (<c>Vehiculo.Notas</c>) y nada más. Gemelo de
/// <c>GuardarNotaInternaSubcontrataCommand</c>.
///
/// <para>
/// <b>Por qué no es un campo de <c>EditarVehiculoCommand</c>.</b> Aquel es de leer, modificar y escribir: recibe
/// nombre, modelo y matrícula enteros. Con la nota dentro, cualquier llamador que no la enviara (el panel del
/// Vehículo, la importación, uno futuro) la borraría al guardar otra cosa. Este comando es estrecho a propósito:
/// quien edita la nota no reenvía los datos del vehículo, y no puede alterarlos.
/// </para>
///
/// <para>
/// <b>Quién la guarda.</b> Por ser <see cref="ICommand"/>, <c>AutorizacionEscrituraBehavior</c> exige un rol con
/// escritura antes de llegar aquí: ni Consulta ni Cliente. Además el handler exige que el vehículo esté en el
/// alcance de quien guarda, con la misma puerta que <c>EditarVehiculoCommand</c> (los vehículos no tienen un alcance
/// de gestión distinto del de lectura), y responde el mismo «no encontrado» a quien no lo tiene.
/// <paramref name="Version"/> no tiene valor por defecto: la ficha manda la que leyó, y si otra persona guardó entre
/// medias la respuesta es el conflicto de <see cref="ConcurrenciaOptimista"/>, no una nota pisada.
/// (<see cref="Guid.Empty"/> sigue significando «sin comprobación», como en el resto de comandos.)
/// </para>
/// </summary>
/// <param name="Notas">Vacía, solo espacios o <c>null</c>: el vehículo se queda sin nota.</param>
public record GuardarNotaInternaVehiculoCommand(Guid Id, string? Notas, Guid Version) : ICommand;

public class GuardarNotaInternaVehiculoCommandValidator : AbstractValidator<GuardarNotaInternaVehiculoCommand>
{
    public GuardarNotaInternaVehiculoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Notas)
            .MaximumLength(Vehiculo.LongitudMaximaNotas)
            .WithMessage($"La nota interna no puede superar {Vehiculo.LongitudMaximaNotas} caracteres.");
    }
}

public class GuardarNotaInternaVehiculoCommandHandler(
    IVehiculoRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarNotaInternaVehiculoCommand, Result>
{
    public async Task<Result> Handle(GuardarNotaInternaVehiculoCommand request, CancellationToken cancellationToken)
    {
        var vehiculo = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (vehiculo is null || !await alcanceDatos.VehiculoVisibleAsync(vehiculo.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Vehiculo.NoEncontrado", "No encontramos este vehículo."));

        if (ConcurrenciaOptimista.Verificar(vehiculo, request.Version, "este vehículo") is { } conflicto)
            return Result.Fallo(conflicto);

        vehiculo.FijarNotaInterna(request.Notas);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

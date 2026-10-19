using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Trabajadores;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Trabajadores.Commands.GuardarNotaInternaTrabajador;

/// <summary>
/// Guarda la «Nota interna» de la ficha Trabajador 360 (<c>Trabajador.Notas</c>) y nada más. Gemelo de
/// <c>GuardarNotaInternaEmpresaCommand</c>.
///
/// <para>
/// <b>Por qué no es un campo de <c>EditarTrabajadorCommand</c> ni de <c>EditarFichaTrabajadorCommand</c>.</b> Los dos
/// son de leer, modificar y escribir; con la nota dentro, cualquier llamador que no la enviara (el panel, la
/// importación, uno futuro) la borraría al guardar otra cosa. Tampoco es <c>Observaciones</c>, que la API pública v1
/// publica.
/// </para>
///
/// <para>
/// <b>Quién la guarda.</b> Por ser <see cref="ICommand"/>, <c>AutorizacionEscrituraBehavior</c> exige un rol con
/// escritura antes de llegar aquí: ni Consulta ni Cliente. Además el handler exige que el Trabajador esté en el alcance
/// de quien guarda (<see cref="AlcanceDatosServiceExtensions.TrabajadorVisibleAsync"/>; para un Gestor CAE, su
/// Asignación de Cartera), la misma puerta que <c>EditarTrabajadorCommand</c> y <c>EditarFichaTrabajadorCommand</c>
/// (el Trabajador no tiene un alcance de gestión distinto del de lectura), y responde el mismo «no encontrado» a quien
/// no lo tiene. Un Trabajador anonimizado no admite nota: es dato personal (<c>Trabajador.CamposDeDatosPersonales</c>).
/// <paramref name="Version"/> es la que leyó la ficha: si otra persona guardó entre medias, la respuesta es el conflicto
/// de <see cref="ConcurrenciaOptimista"/>.
/// </para>
/// </summary>
/// <param name="Notas">Vacía, solo espacios o <c>null</c>: el Trabajador se queda sin nota.</param>
public record GuardarNotaInternaTrabajadorCommand(Guid Id, string? Notas, Guid Version) : ICommand;

public class GuardarNotaInternaTrabajadorCommandValidator : AbstractValidator<GuardarNotaInternaTrabajadorCommand>
{
    public GuardarNotaInternaTrabajadorCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Notas)
            .MaximumLength(Trabajador.LongitudMaximaNotas)
            .WithMessage($"La nota interna no puede superar {Trabajador.LongitudMaximaNotas} caracteres.");
    }
}

public class GuardarNotaInternaTrabajadorCommandHandler(
    ITrabajadorRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarNotaInternaTrabajadorCommand, Result>
{
    public async Task<Result> Handle(GuardarNotaInternaTrabajadorCommand request, CancellationToken cancellationToken)
    {
        var trabajador = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (trabajador is null || !await alcanceDatos.TrabajadorVisibleAsync(trabajador.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Trabajador.NoEncontrado", "No encontramos este trabajador."));

        if (trabajador.EstaAnonimizado)
            return Result.Fallo(Error.Crear("Trabajador.Anonimizado",
                "Este trabajador está anonimizado: sus datos personales se suprimieron y no pueden volver a introducirse."));

        if (ConcurrenciaOptimista.Verificar(trabajador, request.Version, "este trabajador") is { } conflicto)
            return Result.Fallo(conflicto);

        trabajador.FijarNotaInterna(request.Notas);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

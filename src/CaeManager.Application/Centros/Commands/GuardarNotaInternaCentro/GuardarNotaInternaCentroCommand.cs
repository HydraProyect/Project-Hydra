using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.GuardarNotaInternaCentro;

/// <summary>
/// Guarda la «Nota interna» de la ficha Centro 360 (<c>Centro.Notas</c>) y nada más. Gemelo de
/// <c>GuardarNotaInternaEmpresaCommand</c>.
///
/// <para>
/// <b>Por qué no es un campo de <c>EditarCentroCommand</c> ni de <c>EditarFichaCentroCommand</c>.</b> Los dos son de
/// leer, modificar y escribir; con la nota dentro, cualquier llamador que no la enviara la borraría al guardar otra
/// cosa. Este comando es estrecho a propósito: quien edita la nota no reenvía los datos del Centro.
/// </para>
///
/// <para>
/// <b>Quién la guarda.</b> Por ser <see cref="ICommand"/>, <c>AutorizacionEscrituraBehavior</c> exige un rol con
/// escritura antes de llegar aquí: ni Consulta ni Cliente. Además el handler exige alcance de <b>gestión</b> sobre el
/// Centro (<see cref="AlcanceDatosServiceExtensions.CentroParaGestionVisibleAsync"/>; para un Gestor CAE, su Asignación
/// de Cartera), igual que <c>EditarCentroCommand</c> y <c>EditarFichaCentroCommand</c>, y responde el mismo «no
/// encontrado» a quien no lo tiene; un Centro de otro Tenant ni siquiera lo carga el repositorio (filtro de Tenant y
/// RLS). <paramref name="Version"/> es la que leyó la ficha: si otra persona guardó entre medias, la respuesta es el
/// conflicto de <see cref="ConcurrenciaOptimista"/>.
/// </para>
/// </summary>
/// <param name="Notas">Vacía, solo espacios o <c>null</c>: el Centro se queda sin nota.</param>
public record GuardarNotaInternaCentroCommand(Guid Id, string? Notas, Guid Version) : ICommand;

public class GuardarNotaInternaCentroCommandValidator : AbstractValidator<GuardarNotaInternaCentroCommand>
{
    public GuardarNotaInternaCentroCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Notas)
            .MaximumLength(Centro.LongitudMaximaNotas)
            .WithMessage($"La nota interna no puede superar {Centro.LongitudMaximaNotas} caracteres.");
    }
}

public class GuardarNotaInternaCentroCommandHandler(
    ICentroRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarNotaInternaCentroCommand, Result>
{
    public async Task<Result> Handle(GuardarNotaInternaCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroParaGestionVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        if (ConcurrenciaOptimista.Verificar(centro, request.Version, "este centro") is { } conflicto)
            return Result.Fallo(conflicto);

        centro.FijarNotaInterna(request.Notas);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Empresas.Commands.GuardarNotaInternaEmpresa;

/// <summary>
/// Guarda la «Nota interna» de la ficha Empresa 360 (<c>Empresa.Notas</c>) y nada más.
///
/// <para>
/// <b>Por qué no es un campo de <c>EditarEmpresaCommand</c>.</b> Aquel es de leer, modificar y escribir: con la nota
/// dentro, cualquier llamador que no la enviara la borraría al guardar otra cosa. Este comando es estrecho a propósito:
/// quien edita la nota no reenvía razón social, CIF ni relaciones, y no puede alterarlas.
/// </para>
///
/// <para>
/// <b>Una nota por Empresa.</b> Es la misma columna que escribe <c>GuardarNotaInternaSubcontrataCommand</c> y la de
/// Cliente 360: la Empresa tiene una sola nota, compartida entre sus papeles.
/// </para>
///
/// <para>
/// <b>Quién la guarda.</b> Por ser <see cref="ICommand"/>, <c>AutorizacionEscrituraBehavior</c> exige un rol con
/// escritura antes de llegar aquí: ni Consulta ni Cliente. Además el handler exige alcance de <b>gestión</b> sobre la
/// Empresa, igual que <c>EditarEmpresaCommand</c>, y responde el mismo «no encontrada» a quien no lo tiene.
/// <paramref name="Version"/> no tiene valor por defecto: la ficha manda la que leyó, y si otra persona guardó entre
/// medias la respuesta es el conflicto de <see cref="ConcurrenciaOptimista"/>, no una nota pisada.
/// </para>
/// </summary>
/// <param name="Notas">Vacía, solo espacios o <c>null</c>: la Empresa se queda sin nota.</param>
public record GuardarNotaInternaEmpresaCommand(Guid Id, string? Notas, Guid Version) : ICommand;

public class GuardarNotaInternaEmpresaCommandValidator : AbstractValidator<GuardarNotaInternaEmpresaCommand>
{
    public GuardarNotaInternaEmpresaCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Notas)
            .MaximumLength(Empresa.LongitudMaximaNotas)
            .WithMessage($"La nota interna no puede superar {Empresa.LongitudMaximaNotas} caracteres.");
    }
}

public class GuardarNotaInternaEmpresaCommandHandler(
    IEmpresaRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarNotaInternaEmpresaCommand, Result>
{
    public async Task<Result> Handle(GuardarNotaInternaEmpresaCommand request, CancellationToken cancellationToken)
    {
        var empresa = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (empresa is null || !await alcanceDatos.EmpresaParaGestionVisibleAsync(empresa.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Empresa.NoEncontrada", "No encontramos esta empresa."));

        if (ConcurrenciaOptimista.Verificar(empresa, request.Version, "esta empresa") is { } conflicto)
            return Result.Fallo(conflicto);

        empresa.FijarNotaInterna(request.Notas);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

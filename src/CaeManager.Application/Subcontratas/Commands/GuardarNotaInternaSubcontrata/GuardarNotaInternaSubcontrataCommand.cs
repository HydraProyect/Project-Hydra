using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.GuardarNotaInternaSubcontrata;

/// <summary>
/// Guarda la «Nota interna» de la ficha Subcontrata 360 (<c>Empresa.Notas</c>) y nada más.
///
/// <para>
/// <b>Por qué no es un campo de <c>EditarSubcontrataCommand</c>.</b> Aquel es de leer, modificar y escribir: recibe
/// la identidad y las dos listas de contrapartes enteras, y calcula altas y bajas por diferencia. Con la nota dentro,
/// cualquier llamador que no la enviara (el panel, la importación, uno futuro) la borraría al guardar otra cosa. Este
/// comando es estrecho a propósito: quien edita la nota no reenvía razón social, CIF ni relaciones, y no puede
/// alterarlas.
/// </para>
///
/// <para>
/// <b>Quién la guarda.</b> Por ser <see cref="ICommand"/>, <c>AutorizacionEscrituraBehavior</c> exige un rol con
/// escritura antes de llegar aquí: ni Consulta ni Cliente. Además el handler exige alcance de <b>gestión</b> sobre
/// la subcontrata, igual que <c>EditarSubcontrataCommand</c>, y responde el mismo «no encontrada» a quien no lo
/// tiene. <paramref name="Version"/> no tiene valor por defecto: la ficha manda la que leyó, y si otra persona guardó
/// entre medias la respuesta es el conflicto de <see cref="ConcurrenciaOptimista"/>, no una nota pisada.
/// (<see cref="Guid.Empty"/> sigue significando «sin comprobación», como en el resto de comandos.)
/// </para>
/// </summary>
/// <param name="Notas">Vacía, solo espacios o <c>null</c>: la subcontrata se queda sin nota.</param>
public record GuardarNotaInternaSubcontrataCommand(Guid Id, string? Notas, Guid Version) : ICommand;

public class GuardarNotaInternaSubcontrataCommandValidator : AbstractValidator<GuardarNotaInternaSubcontrataCommand>
{
    public GuardarNotaInternaSubcontrataCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Notas)
            .MaximumLength(Empresa.LongitudMaximaNotas)
            .WithMessage($"La nota interna no puede superar {Empresa.LongitudMaximaNotas} caracteres.");
    }
}

public class GuardarNotaInternaSubcontrataCommandHandler(
    IEmpresaRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<GuardarNotaInternaSubcontrataCommand, Result>
{
    public async Task<Result> Handle(GuardarNotaInternaSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        // Como EditarSubcontrataCommand, no comprueba que la Empresa sea subcontrata: con alcance total la puerta de
        // gestión admite cualquier Id del Tenant, y quien lo tiene ya puede escribir esa misma nota por
        // EditarClienteCommand. Leer aquí el discriminador NivelServicio lo prohíbe
        // RolDeEmpresaPorDiscriminadorNoCreceTests (las lecturas por discriminador solo decrecen).
        if (subcontrata is null || !await alcanceDatos.SubcontrataParaGestionVisibleAsync(subcontrata.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        if (ConcurrenciaOptimista.Verificar(subcontrata, request.Version, "esta subcontrata") is { } conflicto)
            return Result.Fallo(conflicto);

        subcontrata.FijarNotaInterna(request.Notas);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

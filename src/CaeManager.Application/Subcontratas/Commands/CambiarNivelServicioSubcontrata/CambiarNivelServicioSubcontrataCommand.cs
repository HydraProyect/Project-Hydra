using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Subcontratas.Commands.CambiarNivelServicioSubcontrata;

/// <summary>
/// Cambio del nivel de servicio contratado (ADR-005 § 2.1) — operación de
/// negocio explícita, separada de EditarSubcontrata a propósito: cambiar el
/// servicio no es corregir un dato, y el historial (verificaciones externas
/// incluidas) se conserva en ambos sentidos.
/// </summary>
public record CambiarNivelServicioSubcontrataCommand(
    Guid Id, NivelServicioSubcontrata NivelServicio, Guid Version = default) : ICommand;

public class CambiarNivelServicioSubcontrataCommandValidator : AbstractValidator<CambiarNivelServicioSubcontrataCommand>
{
    public CambiarNivelServicioSubcontrataCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.NivelServicio).IsInEnum();
    }
}

public class CambiarNivelServicioSubcontrataCommandHandler(
    IEmpresaRepository repositorio,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork)
    : IRequestHandler<CambiarNivelServicioSubcontrataCommand, Result>
{
    public async Task<Result> Handle(CambiarNivelServicioSubcontrataCommand request, CancellationToken cancellationToken)
    {
        var subcontrata = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (subcontrata is null || !await alcanceDatos.SubcontrataVisibleAsync(subcontrata.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Subcontrata.NoEncontrada", "No encontramos esta subcontrata."));

        if (ConcurrenciaOptimista.Verificar(subcontrata, request.Version, "esta subcontrata") is { } conflicto)
            return Result.Fallo(conflicto);

        subcontrata.CambiarNivelServicioComoSubcontrata(request.NivelServicio.ToString());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

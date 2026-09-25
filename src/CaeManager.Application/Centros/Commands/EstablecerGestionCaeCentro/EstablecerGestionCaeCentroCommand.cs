using CaeManager.Application.Common;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Centros.Commands.EstablecerGestionCaeCentro;

/// <summary>
/// Marca si un Centro de Trabajo exige gestión CAE (P1-X2). Pasar a
/// <see cref="ModalidadGestionCae.SinGestionCae"/> no borra nada: los canales,
/// requisitos y acreditaciones que tuviera se conservan y dejan de contar
/// mientras el Centro no requiera gestión; volver a
/// <see cref="ModalidadGestionCae.ConGestionCae"/> los recupera tal cual.
///
/// Autorización: rol de escritura (<see cref="AutorizacionEscrituraBehavior"/>)
/// y alcance de gestión del Gestor CAE sobre el Centro; fuera de alcance
/// responde igual que un Centro inexistente.
///
/// Auditoría: el cambio silencia el cumplimiento del Centro, así que queda en
/// RegistroAuditoria con Actor real, valor anterior y valor nuevo. Lo escribe
/// el interceptor genérico de auditoría al guardar (fijado por
/// <c>AuditoriaModalidadGestionCaeTests</c>), no este handler.
/// </summary>
public record EstablecerGestionCaeCentroCommand(Guid CentroId, ModalidadGestionCae Modalidad, Guid Version = default) : ICommand;

public class EstablecerGestionCaeCentroCommandValidator : AbstractValidator<EstablecerGestionCaeCentroCommand>
{
    public EstablecerGestionCaeCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.Modalidad).IsInEnum().WithMessage("Modalidad de gestión CAE desconocida.");
    }
}

public class EstablecerGestionCaeCentroCommandHandler(
    ICentroRepository repositorio, IAlcanceDatosService alcanceDatos, IUnitOfWork unitOfWork)
    : IRequestHandler<EstablecerGestionCaeCentroCommand, Result>
{
    public async Task<Result> Handle(EstablecerGestionCaeCentroCommand request, CancellationToken cancellationToken)
    {
        var centro = await repositorio.ObtenerPorIdAsync(request.CentroId, cancellationToken);
        if (centro is null || !await alcanceDatos.CentroParaGestionVisibleAsync(centro.Id, cancellationToken))
            return Result.Fallo(Error.Crear("Centro.NoEncontrado", "No encontramos este centro."));

        if (ConcurrenciaOptimista.Verificar(centro, request.Version, "este centro") is { } conflicto)
            return Result.Fallo(conflicto);

        centro.EstablecerGestionCae(request.Modalidad);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.TiposDocumento.Commands.EditarTipoDocumento;

public record EditarTipoDocumentoCommand(
    Guid Id,
    string Nombre,
    int? VigenciaMeses,
    bool AplicaVencimientoAutomatico,
    int Orden,
    bool EsObligatorio,
    string? Notas,
    string? Descripcion,
    string? CriteriosValidacion,
    string? SeSolicitaA,
    string? Observaciones,
    IReadOnlyList<Guid> CentroIds) : ICommand;

public class EditarTipoDocumentoCommandValidator : AbstractValidator<EditarTipoDocumentoCommand>
{
    public EditarTipoDocumentoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();

        RuleFor(c => c.Nombre)
            .NotEmpty().WithMessage("El nombre es obligatorio.")
            .MaximumLength(TipoDocumento.LongitudMaximaNombre);

        RuleFor(c => c.VigenciaMeses)
            .GreaterThan(0).WithMessage("La vigencia debe ser mayor que cero.")
            .When(c => c.VigenciaMeses is not null);

        RuleFor(c => c.VigenciaMeses)
            .NotNull().WithMessage("Un tipo de documento con vencimiento automático debe tener una vigencia en meses.")
            .When(c => c.AplicaVencimientoAutomatico);

        RuleFor(c => c.Notas).MaximumLength(TipoDocumento.LongitudMaximaNotas);
        RuleFor(c => c.Descripcion).MaximumLength(TipoDocumento.LongitudMaximaDescripcion);
        RuleFor(c => c.CriteriosValidacion).MaximumLength(TipoDocumento.LongitudMaximaCriteriosValidacion);
        RuleFor(c => c.SeSolicitaA).MaximumLength(TipoDocumento.LongitudMaximaSeSolicitaA);
        RuleFor(c => c.Observaciones).MaximumLength(TipoDocumento.LongitudMaximaObservaciones);
    }
}

public class EditarTipoDocumentoCommandHandler(
    ITipoDocumentoRepository repositorio, ITipoDocumentoCentroRepository tipoDocumentoCentroRepositorio,
    ICentrosQueryContext centrosContext, IUnitOfWork unitOfWork)
    : IRequestHandler<EditarTipoDocumentoCommand, Result>
{
    public async Task<Result> Handle(EditarTipoDocumentoCommand request, CancellationToken cancellationToken)
    {
        var tipoDocumento = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("TipoDocumento.NoEncontrado", "No encontramos este tipo de documento."));

        if (await repositorio.ExisteConNombreAsync(request.Nombre, request.Id, cancellationToken))
            return Result.Fallo(Error.Crear("TipoDocumento.NombreDuplicado", "Ya existe un tipo de documento con este nombre."));

        tipoDocumento.Actualizar(
            request.Nombre,
            request.VigenciaMeses,
            request.AplicaVencimientoAutomatico,
            request.Orden,
            request.EsObligatorio,
            request.Notas,
            request.Descripcion,
            request.CriteriosValidacion,
            request.SeSolicitaA,
            request.Observaciones);

        // Solo se tocan las filas Incluido=true (creadas desde este mismo picker) — las
        // Incluido=false son exclusiones explícitas por Centro dadas de alta desde
        // Requisitos del Centro (PLAN-EJECUCION-UX.md § 0.4) y no debe pisarlas este flujo.
        var actuales = (await tipoDocumentoCentroRepositorio.ObtenerPorTipoDocumentoAsync(tipoDocumento.Id, cancellationToken))
            .Where(tc => tc.Incluido)
            .ToList();
        var deseados = request.CentroIds.Distinct().ToHashSet();
        var actualesCentroIds = actuales.Select(tc => tc.CentroId).ToHashSet();

        // Verificación de Ids ajenos — ver P0-1 de docs/business/MATURITY_REVIEW.md
        // (hallazgo de la auditoría de PR #48). Solo hace falta verificar las
        // vinculaciones NUEVAS: las que ya estaban antes ya pasaron por esta
        // comprobación cuando se crearon.
        var centroIdsNuevos = deseados.Except(actualesCentroIds).ToList();
        if (await centrosContext.Centros.Where(c => centroIdsNuevos.Contains(c.Id)).CountAsync(cancellationToken) != centroIdsNuevos.Count)
            return Result.Fallo(Error.Crear("TipoDocumento.CentroNoEncontrado", "Alguno de los centros seleccionados no existe."));

        foreach (var tc in actuales.Where(tc => !deseados.Contains(tc.CentroId)))
            tipoDocumentoCentroRepositorio.Eliminar(tc);

        foreach (var centroId in centroIdsNuevos)
            tipoDocumentoCentroRepositorio.Agregar(new TipoDocumentoCentro(tipoDocumento.Id, centroId));

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

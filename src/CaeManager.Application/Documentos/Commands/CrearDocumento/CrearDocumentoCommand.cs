using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.CrearDocumento;

/// <summary>
/// FechaVencimientoManual solo se usa cuando el TipoDocumento no tiene
/// vencimiento automático (AplicaVencimientoAutomatico = false) — en ese
/// caso no hay vigencia en meses que calcular, así que se acepta la fecha
/// que introduce el usuario. Si el tipo sí es automático, se ignora y se
/// recalcula siempre a partir de la vigencia en meses.
/// </summary>
public record CrearDocumentoCommand(
    Guid TrabajadorId, Guid TipoDocumentoId, DateOnly FechaEmision, DateOnly? FechaVencimientoManual,
    string? ArchivoUrl, string? Comentarios)
    : IRequest<Result<Guid>>;

public class CrearDocumentoCommandValidator : AbstractValidator<CrearDocumentoCommand>
{
    public CrearDocumentoCommandValidator()
    {
        RuleFor(c => c.TrabajadorId).NotEmpty().WithMessage("Selecciona un trabajador.");
        RuleFor(c => c.TipoDocumentoId).NotEmpty().WithMessage("Selecciona un tipo de documento.");
        RuleFor(c => c.FechaEmision)
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de emisión no puede ser futura.");
        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
    }
}

public class CrearDocumentoCommandHandler(
    IDocumentoRepository repositorio, IApplicationDbContext dbContext, IUnitOfWork unitOfWork)
    : IRequestHandler<CrearDocumentoCommand, Result<Guid>>
{
    public async Task<Result<Guid>> Handle(CrearDocumentoCommand request, CancellationToken cancellationToken)
    {
        var tipoDocumento = await dbContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == request.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null)
            return Result.Fallo<Guid>(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(request.FechaEmision, tipoDocumento.VigenciaMeses)
            : request.FechaVencimientoManual;

        var documento = new Documento(
            request.TrabajadorId, request.TipoDocumentoId, request.FechaEmision, fechaVencimiento,
            request.ArchivoUrl, request.Comentarios);

        repositorio.Agregar(documento);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito(documento.Id);
    }
}

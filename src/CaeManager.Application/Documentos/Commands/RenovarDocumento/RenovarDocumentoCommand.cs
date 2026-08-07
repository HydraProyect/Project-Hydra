using CaeManager.Application.Common;
using CaeManager.Application.Proyectos;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.DocumentosIa;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Documentos.Commands.RenovarDocumento;

/// <summary>
/// "Editar" un Documento es, en la práctica, renovarlo: nueva fecha de
/// emisión (y opcionalmente un nuevo archivo). El trabajador y el tipo de
/// documento no cambian — si son incorrectos, se elimina y se crea de nuevo
/// (ver UX_PATTERNS.md, "Cambiar estado").
/// </summary>
public record RenovarDocumentoCommand(
    Guid Id, DateOnly FechaEmision, DateOnly? FechaVencimientoManual, string? ArchivoUrl, string? Comentarios) : ICommand;

public class RenovarDocumentoCommandValidator : AbstractValidator<RenovarDocumentoCommand>
{
    public RenovarDocumentoCommandValidator()
    {
        RuleFor(c => c.Id).NotEmpty();
        RuleFor(c => c.FechaEmision)
            .LessThanOrEqualTo(DateOnly.FromDateTime(DateTime.UtcNow))
            .WithMessage("La fecha de emisión no puede ser futura.");
        RuleFor(c => c.Comentarios).MaximumLength(Documento.LongitudMaximaComentarios);
    }
}

public class RenovarDocumentoCommandHandler(
    IDocumentoRepository repositorio, ITiposDocumentoQueryContext dbContext,
    IAlcanceDatosService alcanceDatos, IProyectosQueryContext proyectosContext,
    ITrabajoAnalisisDocumentoRepository colaAnalisis, ICurrentUserService currentUserService,
    IUnitOfWork unitOfWork)
    : IRequestHandler<RenovarDocumentoCommand, Result>
{
    public async Task<Result> Handle(RenovarDocumentoCommand request, CancellationToken cancellationToken)
    {
        var documento = await repositorio.ObtenerPorIdAsync(request.Id, cancellationToken);
        if (documento is null || !await alcanceDatos.DocumentoVisibleAsync(documento, proyectosContext, cancellationToken))
            return Result.Fallo(Error.Crear("Documento.NoEncontrado", "No encontramos este documento."));

        var tipoDocumento = await dbContext.TiposDocumento
            .FirstOrDefaultAsync(t => t.Id == documento.TipoDocumentoId, cancellationToken);

        if (tipoDocumento is null)
            return Result.Fallo(Error.Crear("Documento.TipoDocumentoNoEncontrado", "No encontramos el tipo de documento asociado."));

        var fechaVencimiento = tipoDocumento.AplicaVencimientoAutomatico
            ? CalculadoraEstadoDocumento.CalcularFechaVencimiento(request.FechaEmision, tipoDocumento.VigenciaMeses)
            : request.FechaVencimientoManual;
        documento.Renovar(request.FechaEmision, fechaVencimiento);

        if (!string.IsNullOrWhiteSpace(request.ArchivoUrl))
        {
            documento.AdjuntarArchivo(request.ArchivoUrl);

            // La mensualidad entra por aquí, no por Crear: el certificado de
            // agosto reemplaza al de julio renovando el mismo Documento. Sin
            // este reencolado, el archivo nuevo jamás se validaría. Mismo
            // SaveChangesAsync que el resto del cambio — se confirman juntos
            // o ninguno, la garantía de CrearDocumentoCommand. Los análisis
            // IA (VerificacionIa/DeteccionTrabajadores) siguen sin reencolarse
            // al renovar — decisión pendiente aparte, por su coste de LLM en
            // cada renovación (plan de la épica, PR-3).
            if (tipoDocumento.PerfilDocumentoOficial != PerfilDocumentoOficial.Ninguno)
            {
                var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
                colaAnalisis.Agregar(new TrabajoAnalisisDocumento(
                    documento.Id, usuarioId, TipoAnalisisDocumento.VerificacionFirmaDigital));
            }
        }

        documento.ActualizarComentarios(request.Comentarios);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

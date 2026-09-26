using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Documentos.Acreditacion;
using CaeManager.Application.TiposDocumento;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using FluentValidation;
using MediatR;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Application.Centros.Commands.EstablecerDocumentacionRequeridaCentro;

/// <summary>
/// Alta/edición de la posición explícita de un Centro sobre un TipoDocumento
/// (Project-Hydra-Negocio/tecnico/docs/ux-audit/PLAN-EJECUCION-UX.md § 0.4) — upsert sobre la fila única (TipoDocumentoId,
/// CentroId) de <see cref="TipoDocumentoCentro"/>. Sin <c>Version</c>: la
/// entidad no la tiene (EntidadConTenant, no EntidadBase — "sin ciclo de vida
/// propio", mismo criterio que el resto de altas/bajas de esta tabla puente
/// desde /tipos-documento).
/// </summary>
public record EstablecerDocumentacionRequeridaCentroCommand(
    Guid CentroId,
    Guid TipoDocumentoId,
    bool Incluido,
    int? PeriodicidadEspecialMeses,
    bool BloqueaAcceso,
    string? ArchivoUrl,
    string? NombreArchivoOriginal) : ICommand;

public class EstablecerDocumentacionRequeridaCentroCommandValidator : AbstractValidator<EstablecerDocumentacionRequeridaCentroCommand>
{
    public EstablecerDocumentacionRequeridaCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.PeriodicidadEspecialMeses).GreaterThan(0).When(c => c.PeriodicidadEspecialMeses is not null)
            .WithMessage("La periodicidad especial debe ser un número entero de meses mayor que cero.");
        RuleFor(c => c.ArchivoUrl).MaximumLength(TipoDocumentoCentro.LongitudMaximaArchivoUrl);
        RuleFor(c => c.NombreArchivoOriginal).MaximumLength(TipoDocumentoCentro.LongitudMaximaNombreArchivo);
    }
}

public class EstablecerDocumentacionRequeridaCentroCommandHandler(
    ITipoDocumentoCentroRepository repositorio,
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IUnitOfWork unitOfWork)
    : IRequestHandler<EstablecerDocumentacionRequeridaCentroCommand, Result>
{
    public async Task<Result> Handle(EstablecerDocumentacionRequeridaCentroCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md.
        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.CentroNoEncontrado", "No encontramos este centro."));

        if (!await tiposDocumentoContext.TiposDocumento.AnyAsync(t => t.Id == request.TipoDocumentoId, cancellationToken))
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        var fila = await repositorio.ObtenerPorParAsync(request.TipoDocumentoId, request.CentroId, cancellationToken);

        if (fila is null)
        {
            fila = new TipoDocumentoCentro(
                request.TipoDocumentoId, request.CentroId, request.Incluido,
                request.PeriodicidadEspecialMeses, request.BloqueaAcceso, request.ArchivoUrl, request.NombreArchivoOriginal);
            repositorio.Agregar(fila);
        }
        else
        {
            fila.Actualizar(
                request.Incluido, request.PeriodicidadEspecialMeses, request.BloqueaAcceso,
                request.ArchivoUrl, request.NombreArchivoOriginal);
        }

        // Si el Centro pasa a exigir el tipo, los Documentos de ese tipo de
        // quienes ya trabajan en él nacen pendientes de acreditar ante sus
        // accesos de plataforma. Si deja de exigirlo, no se retira ninguna
        // acreditación (ver IAltaAcreditacionesPlataformaService). Mismo
        // SaveChangesAsync que la fila.
        await altaAcreditaciones.AgregarPendientesAsync(new AltasConAcreditacion { Requisitos = [fila] }, cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return Result.Exito();
    }
}

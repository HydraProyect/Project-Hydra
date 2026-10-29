using CaeManager.Application.Centros;
using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
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
    string? NombreArchivoOriginal,
    int? ToleranciaDias) : ICommand;

public class EstablecerDocumentacionRequeridaCentroCommandValidator : AbstractValidator<EstablecerDocumentacionRequeridaCentroCommand>
{
    public EstablecerDocumentacionRequeridaCentroCommandValidator()
    {
        RuleFor(c => c.CentroId).NotEmpty();
        RuleFor(c => c.TipoDocumentoId).NotEmpty();
        RuleFor(c => c.PeriodicidadEspecialMeses).GreaterThan(0).When(c => c.PeriodicidadEspecialMeses is not null)
            .WithMessage("La periodicidad especial debe ser un número entero de meses mayor que cero.");
        RuleFor(c => c.ToleranciaDias).InclusiveBetween(0, TipoDocumentoCentro.ToleranciaMaximaDias).When(c => c.ToleranciaDias is not null)
            .WithMessage($"La tolerancia debe ser un número entero de días entre 0 y {TipoDocumentoCentro.ToleranciaMaximaDias}, o vacía para heredar la del Cliente.");
        RuleFor(c => c.ArchivoUrl).MaximumLength(TipoDocumentoCentro.LongitudMaximaArchivoUrl);
        RuleFor(c => c.NombreArchivoOriginal).MaximumLength(TipoDocumentoCentro.LongitudMaximaNombreArchivo);
    }
}

public class EstablecerDocumentacionRequeridaCentroCommandHandler(
    ITipoDocumentoCentroRepository repositorio,
    ICentrosQueryContext centrosContext,
    ITiposDocumentoQueryContext tiposDocumentoContext,
    IAltaAcreditacionesPlataformaService altaAcreditaciones,
    IAlcanceDatosService alcanceDatos,
    IUnitOfWork unitOfWork,
    IVerificadorArchivoAlmacenado verificadorArchivo)
    : IRequestHandler<EstablecerDocumentacionRequeridaCentroCommand, Result>
{
    public async Task<Result> Handle(EstablecerDocumentacionRequeridaCentroCommand request, CancellationToken cancellationToken)
    {
        // Verificación de Ids ajenos — ver P0-1 de Project-Hydra-Negocio/MATURITY_REVIEW.md —, y
        // Asignación de Cartera: fuera del alcance de gestión el Centro «no existe» (mismo criterio
        // que EditarCentroCommand).
        if (!await centrosContext.Centros.AnyAsync(c => c.Id == request.CentroId, cancellationToken)
            || !await alcanceDatos.CentroParaGestionVisibleAsync(request.CentroId, cancellationToken))
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.CentroNoEncontrado", "No encontramos este centro."));

        var tipo = await tiposDocumentoContext.TiposDocumento
            .Where(t => t.Id == request.TipoDocumentoId)
            .Select(t => new { t.Archivado })
            .FirstOrDefaultAsync(cancellationToken);
        if (tipo is null)
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.TipoDocumentoNoEncontrado", "No encontramos este tipo de documento."));

        // Un tipo archivado (catálogo revisado el 2026-10-17) no se empieza a exigir en un Centro: se exige su
        // sustituto. Dejar de exigirlo sí se permite, para poder limpiar la configuración que ya lo tenía.
        if (tipo.Archivado && request.Incluido)
            return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.TipoDocumentoArchivado", "Ese tipo de documento está archivado; exige su sustituto."));

        var fila = await repositorio.ObtenerPorParAsync(request.TipoDocumentoId, request.CentroId, cancellationToken);

        // OWASP ASVS 4.0.3 V12.1/V12.2: la clave de archivo y el nombre original llegan del cliente; se validan en
        // servidor (el formulario Blazor ya lo hace, otros llamadores no). El endpoint de descarga deriva del nombre
        // original el nombre y el tipo del archivo, así que la clave y el nombre tienen que terminar en la misma
        // extensión. Una clave nueva solo admite PDF o Word, con la firma y la estructura del contenido almacenado. El
        // contenido de una clave que la fila ya tenía no se vuelve a exigir: editar la tolerancia o excluir el tipo no
        // debe quedar bloqueado por un archivo anterior a esta validación.
        if (request.ArchivoUrl is { } archivoUrl)
        {
            var cambiaClave = archivoUrl != fila?.ArchivoUrl;
            var cambiaNombre = request.NombreArchivoOriginal != fila?.NombreArchivoOriginal;
            if (((cambiaClave || cambiaNombre) && !ExtensionCoincide(archivoUrl, request.NombreArchivoOriginal))
                || (cambiaClave && !await ArchivoRequisitoEsValidoAsync(archivoUrl, cancellationToken)))
                return Result.Fallo(Error.Crear("DocumentacionRequeridaCentro.ArchivoNoValido", ValidadorFirmaArchivo.MensajeRequisitoNoValido));
        }
        if (fila is null)
        {
            fila = new TipoDocumentoCentro(
                request.TipoDocumentoId, request.CentroId, request.Incluido,
                request.PeriodicidadEspecialMeses, request.BloqueaAcceso, request.ArchivoUrl, request.NombreArchivoOriginal,
                request.ToleranciaDias);
            repositorio.Agregar(fila);
        }
        else
        {
            fila.Actualizar(
                request.Incluido, request.PeriodicidadEspecialMeses, request.BloqueaAcceso,
                request.ArchivoUrl, request.NombreArchivoOriginal, request.ToleranciaDias);
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

    private static bool ExtensionCoincide(string archivoUrl, string? nombreOriginal) =>
        nombreOriginal is not null
        && string.Equals(Path.GetExtension(nombreOriginal), Path.GetExtension(archivoUrl), StringComparison.OrdinalIgnoreCase);

    private async Task<bool> ArchivoRequisitoEsValidoAsync(string archivoUrl, CancellationToken cancellationToken) =>
        Path.GetExtension(archivoUrl).ToLowerInvariant() is ".pdf" or ".docx"
        && await verificadorArchivo.EsDocumentoValidoAsync(archivoUrl, cancellationToken);
}
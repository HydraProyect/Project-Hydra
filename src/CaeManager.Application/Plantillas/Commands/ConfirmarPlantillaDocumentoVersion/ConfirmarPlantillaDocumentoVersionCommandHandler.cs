using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plantillas;
using MediatR;

namespace CaeManager.Application.Plantillas.Commands.ConfirmarPlantillaDocumentoVersion;

/// <summary>
/// Auditoría de seguridad del módulo (2026-08-30), pendientes 3.2+3.3 (una
/// sola pieza de trabajo, "compilación de plantilla"): confirmar solo exigía
/// que hubiera algún elemento configurado, sin cotejar los
/// <c>NombreCampoAcroForm</c> contra los campos reales del PDF — un elemento
/// mal configurado pasaba la confirmación y luego
/// <c>RellenadorPlantillaPdfService</c> lo descartaba en silencio al generar
/// (el documento salía con ese campo en blanco, sin ningún error visible).
///
/// Decisión de producto tomada en esta sesión (autorización expresa de
/// trabajo autónomo): la validación aplica SOLO hacia delante — a partir de
/// esta versión del código, para cualquier versión que se confirme (incluida
/// una versión ya existente en estado no confirmado). Las versiones que YA
/// estaban en <see cref="EstadoConfiguracionPlantilla.Confirmada"/> antes de
/// este cambio NO se re-validan retroactivamente: hacerlo podría invalidar
/// plantillas que llevan meses generando documentos correctamente, y una
/// discrepancia real solo se manifestaría si el PDF original cambiase por
/// fuera del flujo normal (no hay mecanismo que lo permita hoy). Revisar
/// esta decisión con el propietario del producto si aparece evidencia de que
/// hace falta una re-validación retroactiva.
/// </summary>
public class ConfirmarPlantillaDocumentoVersionCommandHandler(
    IPlantillaDocumentoVersionRepository versionRepositorio,
    IPlantillaDocumentoRepository documentoRepositorio,
    ICurrentUserService usuarioActual,
    IFileStorageService almacenamiento,
    IExtractorCamposAcroFormService extractorAcroForm,
    IUnitOfWork unitOfWork)
    : IRequestHandler<ConfirmarPlantillaDocumentoVersionCommand, Result>
{
    public async Task<Result> Handle(ConfirmarPlantillaDocumentoVersionCommand request, CancellationToken cancellationToken)
    {
        var version = await versionRepositorio.ObtenerPorIdAsync(request.PlantillaDocumentoVersionId, cancellationToken);
        if (version is null)
            return Result.Fallo(Error.Crear("Plantilla.VersionNoEncontrada", "No encontramos esta versión de plantilla."));

        if (version.EstadoConfiguracion == EstadoConfiguracionPlantilla.Confirmada)
            return Result.Fallo(Error.Crear("Plantilla.VersionYaConfirmada", "Esta versión ya está confirmada."));

        if (version.Elementos.Count == 0)
            return Result.Fallo(Error.Crear(
                "Plantilla.SinElementos", "Añade al menos un campo antes de confirmar la plantilla."));

        var usuarioId = await usuarioActual.ObtenerUsuarioActualIdAsync();
        if (usuarioId is not { } idUsuario)
            return Result.Fallo(Error.Crear("Plantilla.SinUsuarioActual", "No pudimos identificar quién confirma esta plantilla."));

        var documento = await documentoRepositorio.ObtenerPorIdAsync(version.PlantillaDocumentoId, cancellationToken);
        if (documento is null)
            return Result.Fallo(Error.Crear("Plantilla.NoEncontrada", "No encontramos la plantilla de esta versión."));

        if (documento.FormatoOrigen == FormatoOrigenPlantilla.PdfConCampos)
        {
            var resultadoCotejo = await CotejarContraPdfRealAsync(version, cancellationToken);
            if (resultadoCotejo.EsFallido)
                return resultadoCotejo;
        }

        version.Confirmar(idUsuario, DateTime.UtcNow);
        documento.EstablecerVersionActual(version.Id);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    /// <summary>
    /// Cotejo por nombre de campo (no por página/posición: para
    /// <see cref="FormatoOrigenPlantilla.PdfConCampos"/> la posición real la
    /// define el propio PDF, no la fila de <see cref="PlantillaElemento"/> —
    /// ver su doc-comment) contra el PDF exacto que se guardó con esta
    /// versión. Dos formas de quedar mal configurado: apuntar a un campo que
    /// no existe (o venir sin nombre) y que dos elementos apunten al MISMO
    /// campo (uno de los dos pisaría el valor del otro en generación, sin
    /// aviso).
    /// </summary>
    private async Task<Result> CotejarContraPdfRealAsync(PlantillaDocumentoVersion version, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(version.ArchivoOriginalUrl))
            return Result.Fallo(Error.Crear(
                "Plantilla.SinArchivoOriginal", "Esta versión no tiene un PDF original contra el que cotejar los campos."));

        var elementosConCampo = version.Elementos.Where(e => e.Tipo != TipoElementoPlantilla.Firma).ToList();

        byte[] contenido;
        await using (var flujo = await almacenamiento.AbrirAsync(version.ArchivoOriginalUrl, cancellationToken))
        using (var memoria = new MemoryStream())
        {
            await flujo.CopyToAsync(memoria, cancellationToken);
            contenido = memoria.ToArray();
        }

        var camposReales = extractorAcroForm.Extraer(contenido)
            .Select(c => c.NombreCampo)
            .ToHashSet(StringComparer.Ordinal);

        var faltantes = elementosConCampo
            .Where(e => string.IsNullOrWhiteSpace(e.NombreCampoAcroForm) || !camposReales.Contains(e.NombreCampoAcroForm))
            .Select(e => e.EtiquetaVisible)
            .ToList();
        if (faltantes.Count > 0)
            return Result.Fallo(Error.Crear(
                "Plantilla.CamposAcroFormInexistentes",
                $"Estos campos no existen en el PDF: {string.Join(", ", faltantes)}. Revisa la configuración antes de confirmar."));

        var duplicados = elementosConCampo
            .GroupBy(e => e.NombreCampoAcroForm, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => $"{g.Key} ({string.Join(", ", g.Select(e => e.EtiquetaVisible))})")
            .ToList();
        if (duplicados.Count > 0)
            return Result.Fallo(Error.Crear(
                "Plantilla.CamposAcroFormDuplicados",
                $"Varios elementos apuntan al mismo campo del PDF, uno pisaría el valor del otro: {string.Join("; ", duplicados)}."));

        return Result.Exito();
    }
}

using System.Resources;
using CaeManager.Application.Common;
using CaeManager.Application.Common.Archivos;
using CaeManager.Domain.Common;

namespace CaeManager.Application.Subcontratas;

internal static class ValidacionEvidenciaVerificacionExterna
{
    private static readonly ResourceManager Textos = new(
        "CaeManager.Application.Subcontratas.Recursos.ErroresVerificacionExterna",
        typeof(ValidacionEvidenciaVerificacionExterna).Assembly);

    public static Result Validar(byte[] contenido, string? nombreArchivo)
    {
        var extension = Path.GetExtension(nombreArchivo)?.ToLowerInvariant();
        if (contenido.Length is > 0 and <= LimitesArchivoSubido.TamanoMaximoBytes
            && extension is ".pdf" or ".jpg" or ".jpeg" or ".png"
            && ValidadorFirmaArchivo.TieneFirmaValida(contenido, nombreArchivo!))
            return Result.Exito();

        return Result.Fallo(Error.Crear("VerificacionExterna.EvidenciaFormatoNoValido",
            Textos.GetString("EvidenciaFormatoNoValido")!));
    }
}

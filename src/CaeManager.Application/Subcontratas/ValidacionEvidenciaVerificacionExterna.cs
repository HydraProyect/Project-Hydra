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
        if (Path.GetExtension(nombreArchivo)?.ToLowerInvariant() is ".pdf" or ".jpg" or ".jpeg" or ".png"
            && ValidadorFirmaArchivo.EsDocumentoValido(contenido, nombreArchivo))
            return Result.Exito();

        return Result.Fallo(Error.Crear("VerificacionExterna.EvidenciaFormatoNoValido",
            Textos.GetString("EvidenciaFormatoNoValido")!));
    }
}

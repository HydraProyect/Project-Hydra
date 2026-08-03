using System.IO.Compression;

namespace CaeManager.Web.Documentos;

/// <summary>
/// Extrae los archivos de un .zip para la subida múltiple de Documentos
/// (Features/Documentos/Pages/SubidaMasiva.razor) — un solo nivel, no
/// recursivo: un .zip dentro de otro .zip se trata como una entrada más y
/// se descarta más adelante por extensión no reconocida (mismo criterio que
/// cualquier otro tipo no soportado), evitar la complejidad de un
/// zip-bomb anidado no justifica el caso de uso real (varios PDFs/fotos
/// sueltos comprimidos juntos).
/// </summary>
public static class ExtractorZip
{
    public static bool EsZip(string nombreArchivo) =>
        nombreArchivo.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Directorios (entradas que terminan en "/" o sin contenido) se omiten
    /// en silencio — son estructura, no archivos que subir.
    /// </summary>
    public static IReadOnlyList<(byte[] Contenido, string NombreArchivo)> Extraer(byte[] contenidoZip)
    {
        var resultado = new List<(byte[], string)>();

        using var flujo = new MemoryStream(contenidoZip);
        using var archivo = new ZipArchive(flujo, ZipArchiveMode.Read);

        foreach (var entrada in archivo.Entries)
        {
            if (entrada.Length == 0 || entrada.FullName.EndsWith('/'))
                continue;

            using var flujoEntrada = entrada.Open();
            using var memoria = new MemoryStream();
            flujoEntrada.CopyTo(memoria);

            // Solo el nombre, no la ruta interna del zip (carpetas dentro
            // del propio comprimido) — es lo único que se muestra al usuario.
            resultado.Add((memoria.ToArray(), Path.GetFileName(entrada.FullName)));
        }

        return resultado;
    }
}

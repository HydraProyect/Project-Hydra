using System.Security.Cryptography;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace CaeManager.Infrastructure.DocumentosIa;

/// <summary>
/// Clasificación local de documentos (Fase 1 de docs/ARQUITECTURA-IA-DOCUMENTAL.md),
/// sin ninguna llamada a IA. Reutiliza PdfSharp (ya dependencia del proyecto
/// para generar/combinar PDFs, ver ConversorArchivosPdf) en vez de añadir una
/// librería nueva de extracción de texto — su lector de contenido
/// (<see cref="ContentReader"/>) ya expone los operadores de cada página, así
/// que detectar "¿esta página tiene texto real?" es preguntar si aparece
/// algún operador de mostrar texto (Tj/TJ/'/") en su secuencia de contenido,
/// sin necesitar una API de extracción de texto dedicada.
/// </summary>
public class PdfSharpClasificadorDocumentoService(
    ILogger<PdfSharpClasificadorDocumentoService> logger) : IClasificadorDocumentoService
{
    private static readonly string[] ExtensionesImagen = [".jpg", ".jpeg", ".png", ".tif", ".tiff", ".heic", ".bmp", ".gif"];
    private static readonly HashSet<string> OperadoresDeTexto = new(StringComparer.Ordinal) { "Tj", "TJ", "'", "\"" };

    public Task<Result<ClasificacionDocumentoDto>> ClasificarAsync(
        byte[] contenido, string nombreArchivo, CancellationToken cancellationToken = default)
    {
        if (EsImagen(nombreArchivo))
        {
            return Task.FromResult(Result.Exito(
                new ClasificacionDocumentoDto(TipoContenidoDocumento.Imagen, TotalPaginas: 1, PaginasConTextoDigital: [false])));
        }

        try
        {
            using var flujo = new MemoryStream(contenido);
            using var documento = PdfReader.Open(flujo, PdfDocumentOpenMode.Import);

            var paginasConTexto = documento.Pages.Cast<PdfPage>().Select(TienePaginaTextoDigital).ToList();
            var tipo = paginasConTexto.Count == 0 ? TipoContenidoDocumento.Escaneado
                : paginasConTexto.All(tieneTexto => tieneTexto) ? TipoContenidoDocumento.Digital
                : paginasConTexto.All(tieneTexto => !tieneTexto) ? TipoContenidoDocumento.Escaneado
                : TipoContenidoDocumento.Mixto;

            return Task.FromResult(Result.Exito(
                new ClasificacionDocumentoDto(tipo, paginasConTexto.Count, paginasConTexto)));
        }
        catch (Exception ex)
        {
            // El Result que devolvemos lleva microcopy para el usuario, que a
            // propósito no dice nada técnico. Sin registrar la excepción aquí
            // se pierde el único rastro de por qué un PDF concreto no se pudo
            // clasificar (PDF cifrado, corrupto, formato no soportado...).
            //
            // No se interpola nombreArchivo: el nombre de archivo que sube un
            // tenant puede llevar dato de categoría especial (art. 9 RGPD) —
            // nombre de trabajador, DNI, tipo de aptitud médica. En su lugar
            // se registra el mismo hash SHA-256 del contenido que
            // DocumentAIRouterService ya guarda en AuditoriaExtraccionIa para
            // este mismo fallo (mismo criterio que cerró el hueco equivalente
            // en el Módulo 3, PR #383), así que el hash sigue permitiendo
            // correlacionar el registro con la fila de auditoría.
            logger.LogError(ex, "No se pudo clasificar el archivo (hash {HashContenido}).", CalcularHash(contenido));

            return Task.FromResult(Result.Fallo<ClasificacionDocumentoDto>(Error.Crear(
                "ClasificacionDocumento.ArchivoInvalido", "No pudimos leer este archivo para clasificarlo.")));
        }
    }

    private static string CalcularHash(byte[] contenido) => Convert.ToHexStringLower(SHA256.HashData(contenido));

    private static bool EsImagen(string nombreArchivo) =>
        ExtensionesImagen.Any(ext => nombreArchivo.EndsWith(ext, StringComparison.OrdinalIgnoreCase));

    private static bool TienePaginaTextoDigital(PdfPage pagina)
    {
        var secuencia = ContentReader.ReadContent(pagina);
        return Aplanar(secuencia).OfType<COperator>().Any(operador => OperadoresDeTexto.Contains(operador.OpCode.Name));
    }

    /// <summary>Los operadores de contenido pueden anidarse (p. ej. dentro de un bloque BT/ET) — se recorre en profundidad.</summary>
    private static IEnumerable<CObject> Aplanar(CSequence secuencia)
    {
        foreach (var objeto in secuencia)
        {
            yield return objeto;
            if (objeto is CSequence interna)
            {
                foreach (var hijo in Aplanar(interna))
                    yield return hijo;
            }
        }
    }
}

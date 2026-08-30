using System.Security.Cryptography;
using System.Text;

namespace CaeManager.Web.Documentos;

/// <summary>
/// Convierte el nombre de un archivo subido en una referencia que se puede
/// escribir en una traza sin llevarse el dato personal con él.
///
/// El nombre original no es un dato técnico: en operativa real llega como
/// "RECONOCIMIENTO MEDICO - JUAN PEREZ 12345678Z.pdf". Escribirlo en un log
/// duplica nombre, DNI y a veces la naturaleza médica del documento —
/// categoría especial del art. 9 RGPD— en Seq, en los ficheros de log, en
/// sus backups y en las trazas de error, cada uno con una retención propia
/// que no tiene nada que ver con la del documento.
///
/// Lo que se conserva es lo que sirve para diagnosticar: un hash corto y
/// estable, que permite correlacionar todas las líneas del mismo archivo
/// dentro de una incidencia, y la extensión, que dice qué ruta de
/// conversión falló. Ni el hash ni la extensión reconstruyen el nombre.
///
/// El hash no está salado a propósito: es un identificador de correlación,
/// no un secreto. Un nombre de archivo tiene poquísima entropía y cualquier
/// sal que lo protegiera de verdad rompería la correlación entre procesos,
/// que es justo para lo que existe.
/// </summary>
public static class ReferenciaArchivoTraza
{
    public static string De(string? nombreArchivo)
    {
        if (string.IsNullOrWhiteSpace(nombreArchivo))
            return "archivo:sin-nombre";

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(nombreArchivo));
        var resumen = Convert.ToHexString(hash.AsSpan(0, 4)).ToLowerInvariant();

        // La extensión sí es técnica (decide la ruta de conversión) y no
        // identifica a nadie. Se normaliza para que un ".PDF" y un ".pdf"
        // no parezcan dos casos distintos al agrupar en Seq.
        var extension = Path.GetExtension(nombreArchivo).ToLowerInvariant();

        return string.IsNullOrEmpty(extension)
            ? $"archivo:{resumen}"
            : $"archivo:{resumen}{extension}";
    }
}

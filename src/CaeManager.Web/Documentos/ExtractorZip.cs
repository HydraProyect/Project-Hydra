using System.Buffers;
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
///
/// <b>Límites de descompresión</b>: el tope de 10 MB de la subida acota el
/// .zip comprimido, no lo que sale de él. Un .zip de 99,7 KB de ceros
/// produce 100 MB — factor 1027, medido — así que un archivo dentro del
/// límite de subida podía expandirse a más de 10 GB dentro del proceso web,
/// y el lote admite 60 archivos. El resultado era OOM y la caída de todos
/// los circuitos Blazor de la réplica, cruzando tenants.
///
/// Los límites se llevan <b>durante</b> la copia y sobre los bytes
/// realmente leídos. El motivo NO es que la cabecera del .zip se pueda
/// falsificar para producir más de lo declarado: eso se comprobó y es falso
/// en <c>System.IO.Compression</c> — <c>ZipArchiveEntry.Open()</c> acota la
/// lectura al tamaño sin comprimir declarado, así que un .zip con 40 MB
/// reales declarados como 1 KB entrega 1024 bytes (medido, fijado en
/// ExtractorZipTests). El rechazo por cabecera es, por tanto, una cota
/// fiable.
///
/// Se cuenta durante la copia por dos razones distintas: el presupuesto es
/// acumulativo entre entradas y entre archivos del mismo lote, cosa que
/// ninguna cabecera individual dice; y así el control no depende de una
/// garantía del framework que podría cambiar sin avisar. La cabecera evita
/// descomprimir lo que ya se delata; el recuento real es lo que acota.
///
/// No se comprueba el ratio de compresión: sobre valores declarados no
/// añade garantía (los elige el atacante) y sobre contenido legítimo muy
/// comprimible —un escaneado en blanco— produce falsos positivos. El
/// presupuesto de bytes reales acota el daño por sí solo.
/// </summary>
public static class ExtractorZip
{
    public static bool EsZip(string nombreArchivo) =>
        nombreArchivo.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Directorios (entradas que terminan en "/" o sin contenido) se omiten
    /// en silencio — son estructura, no archivos que subir.
    /// </summary>
    /// <param name="maximoEntradas">
    /// Cuántos archivos puede traer el .zip. Se comprueba antes de
    /// descomprimir nada: el llamador ya rechazaba los lotes de más de N
    /// archivos, pero lo hacía después de haberlos expandido en memoria.
    /// </param>
    /// <param name="maximoPorEntrada">
    /// Tope por archivo extraído, el mismo que el llamador aplica a un
    /// archivo suelto. Descomprimir más para descartarlo después es
    /// justamente el gasto que hay que evitar.
    /// </param>
    /// <param name="presupuestoTotalBytes">
    /// Bytes descomprimidos que quedan disponibles para este .zip dentro del
    /// presupuesto del lote completo (ver SubidaMasiva). Lo que ya gastaron
    /// los archivos anteriores del mismo lote no vuelve a estar disponible.
    /// </param>
    /// <exception cref="InvalidDataException">
    /// El .zip supera alguno de los tres límites. El llamador lo trata igual
    /// que un .zip ilegible: se marca el archivo con error y el lote sigue.
    /// </exception>
    public static IReadOnlyList<(byte[] Contenido, string NombreArchivo)> Extraer(
        byte[] contenidoZip,
        int maximoEntradas,
        long maximoPorEntrada,
        long presupuestoTotalBytes)
    {
        var resultado = new List<(byte[], string)>();

        using var flujo = new MemoryStream(contenidoZip);
        using var archivo = new ZipArchive(flujo, ZipArchiveMode.Read);

        var entradas = archivo.Entries
            .Where(e => e.Length > 0 && !e.FullName.EndsWith('/'))
            .ToList();

        if (entradas.Count > maximoEntradas)
            throw new InvalidDataException(
                $"El .zip contiene {entradas.Count} archivos y el máximo por subida es {maximoEntradas}.");

        var presupuestoRestante = presupuestoTotalBytes;

        foreach (var entrada in entradas)
        {
            // Rechazo barato por cabecera: si el propio .zip ya declara más
            // de lo permitido, no hace falta abrir la entrada. Que sea
            // falsificable no lo hace inútil — solo insuficiente, y por eso
            // el tope real se aplica abajo sobre los bytes leídos.
            if (entrada.Length > maximoPorEntrada)
                throw new InvalidDataException(
                    "Un archivo del .zip supera el máximo por archivo.");

            using var flujoEntrada = entrada.Open();
            using var memoria = new MemoryStream();

            var bufer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                int leidos;
                while ((leidos = flujoEntrada.Read(bufer, 0, bufer.Length)) > 0)
                {
                    if (memoria.Length + leidos > maximoPorEntrada)
                        throw new InvalidDataException(
                            "Un archivo del .zip supera el máximo por archivo al descomprimirse.");

                    if (leidos > presupuestoRestante)
                        throw new InvalidDataException(
                            "El contenido descomprimido del .zip supera el máximo admitido para una subida.");

                    presupuestoRestante -= leidos;
                    memoria.Write(bufer, 0, leidos);
                }
            }
            finally
            {
                // clearArray: el búfer ha tenido contenido de un documento de
                // trabajador y vuelve a un pool compartido con el resto del
                // proceso.
                ArrayPool<byte>.Shared.Return(bufer, clearArray: true);
            }

            // Solo el nombre, no la ruta interna del zip (carpetas dentro
            // del propio comprimido) — es lo único que se muestra al usuario.
            var nombreArchivo = Path.GetFileName(entrada.FullName);
            if (string.IsNullOrWhiteSpace(nombreArchivo))
                continue;

            resultado.Add((memoria.ToArray(), nombreArchivo));
        }

        return resultado;
    }
}

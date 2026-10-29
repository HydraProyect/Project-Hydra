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
/// acumulativo entre entradas, entre archivos y entre lotes de la misma
/// pantalla (es la reserva de <see cref="AlmacenTemporalSubidaMasiva"/>), cosa
/// que ninguna cabecera individual dice; y así el control no depende de una
/// garantía del framework que podría cambiar sin avisar. La cabecera evita
/// descomprimir lo que ya se delata; el recuento real es lo que acota.
///
/// Las entradas se vuelcan a temporales del almacén, no a memoria (S-C-3,
/// ASVS 4.0.3 V12.1.1): el .zip se lee desde su propio temporal y cada entrada
/// se copia en trozos, así que extraer 60 archivos de 10 MB no retiene nunca
/// más de un búfer en memoria.
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
    /// <param name="contenidoZip">El .zip, legible y con posicionamiento; no se cierra. Se lee de forma síncrona, como antes el <c>byte[]</c>: está en el disco de este servidor.</param>
    /// <param name="maximoEntradas">
    /// Cuántos archivos puede traer aún este .zip: son las plazas que quedan
    /// en el lote, no el tope de la subida. Se comprueba antes de descomprimir
    /// nada — el llamador ya rechazaba los lotes de más de N archivos, pero lo
    /// hacía después de haberlos expandido en memoria.
    /// </param>
    /// <param name="maximoPorEntrada">
    /// Tope por archivo extraído, el mismo que el llamador aplica a un
    /// archivo suelto. Descomprimir más para descartarlo después es
    /// justamente el gasto que hay que evitar.
    /// </param>
    /// <param name="almacen">
    /// Donde quedan las entradas. Su reserva es el presupuesto: lo que ya
    /// retienen los archivos anteriores de la misma pantalla no vuelve a estar
    /// disponible. Si la extracción falla, lo ya extraído se borra.
    /// </param>
    /// <exception cref="LimiteDeZipSuperadoException">
    /// El .zip supera alguno de los tres límites. El llamador lo trata igual
    /// que un .zip ilegible: se marca el archivo con error y el lote sigue.
    /// </exception>
    public static IReadOnlyList<(ArchivoTemporal Archivo, string NombreArchivo)> Extraer(
        Stream contenidoZip,
        int maximoEntradas,
        long maximoPorEntrada,
        AlmacenTemporalSubidaMasiva almacen)
    {
        var resultado = new List<(ArchivoTemporal, string)>();
        try
        {
            using var archivo = new ZipArchive(contenidoZip, ZipArchiveMode.Read, leaveOpen: true);

            var entradas = archivo.Entries
                .Where(e => e.Length > 0 && !e.FullName.EndsWith('/'))
                .ToList();

            if (entradas.Count > maximoEntradas)
                // Sin cifra de máximo a propósito: lo que se recibe aquí son las
                // plazas que QUEDAN en el lote, no el tope de la subida, así que
                // anunciarlo como "el máximo es N" mentiría en cuanto el lote
                // llevara algún archivo — al usuario le diría que el límite es 5
                // cuando son 60.
                throw new LimiteDeZipSuperadoException(
                    $"El .zip contiene {entradas.Count} archivos y no caben en esta subida.");

            foreach (var entrada in entradas)
            {
                // Rechazo barato por cabecera: si el propio .zip ya declara más
                // de lo permitido, no hace falta abrir la entrada. Que sea
                // falsificable no lo hace inútil — solo insuficiente, y por eso
                // el tope real se aplica en la copia sobre los bytes leídos.
                if (entrada.Length > maximoPorEntrada)
                    throw new LimiteDeZipSuperadoException(
                        "Un archivo del .zip supera el máximo por archivo.");

                // Solo el nombre, no la ruta interna del zip (carpetas dentro
                // del propio comprimido) — es lo único que se muestra al usuario.
                var nombreArchivo = Path.GetFileName(entrada.FullName);
                if (string.IsNullOrWhiteSpace(nombreArchivo))
                    continue;

                ArchivoTemporal extraido;
                try
                {
                    // Se reserva lo declarado ANTES de abrir la entrada: lo que no
                    // cabe se rechaza sin descomprimir nada.
                    extraido = almacen.Guardar(entrada.Open, entrada.Length, maximoPorEntrada);
                }
                catch (ArchivoTemporalDemasiadoGrandeException)
                {
                    throw new LimiteDeZipSuperadoException(
                        "Un archivo del .zip supera el máximo por archivo al descomprimirse.");
                }
                catch (CuotaSubidaMasivaSuperadaException)
                {
                    throw new LimiteDeZipSuperadoException(
                        "El contenido descomprimido del .zip no cabe en lo que queda de esta subida.");
                }

                resultado.Add((extraido, nombreArchivo));
            }

            return resultado;
        }
        catch
        {
            foreach (var (extraido, _) in resultado)
                almacen.Eliminar(extraido);
            throw;
        }
    }
}

/// <summary>
/// Un .zip de la subida múltiple supera uno de los límites de descompresión. Es el
/// caso previsto, no un fallo, y su mensaje —redactado aquí— es el único de una
/// excepción de la extracción que puede enseñarse al usuario. Tipo propio porque
/// <see cref="ZipArchive"/> lanza <see cref="InvalidDataException"/> con texto
/// técnico ante un .zip dañado, y antes los dos casos compartían tipo y pantalla.
/// </summary>
public sealed class LimiteDeZipSuperadoException(string mensaje) : Exception(mensaje);

using System.IO.Compression;
using CaeManager.Application.Common;
using CaeManager.Domain.Documentos;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Documentos.PaqueteDocumental;

/// <summary>
/// La regla y el armado del zip que sale de TALVEG hacia un Cliente empresarial, compartidos por el paquete de una Visita
/// (<c>PaqueteDocumentalVisitaService</c>) y el «Preparar paquete» de Empresa 360 (<c>PaqueteDocumentalEmpresaService</c>):
/// una sola regla de selección, no dos copias que puedan divergir.
/// </summary>
internal static class ArmadoPaqueteDocumental
{
    /// <summary>Un Documento operativo con archivo que puede viajar.</summary>
    internal sealed record Candidato(
        Guid Id, Guid? TrabajadorId, Guid TipoDocumentoId, string ArchivoUrl, DateOnly FechaEmision,
        EstadoVigenciaDocumento EstadoVigencia, DateOnly? FechaVencimiento, DateTime CreadoEnUtc, bool EsRenuncia);

    /// <summary>Una copia vigente de un (titular, tipo), en orden de preferencia dentro de su grupo.</summary>
    internal sealed record Copia(Guid Id, Guid? TrabajadorId, Guid TipoDocumentoId, string ArchivoUrl);

    /// <summary>
    /// Un grupo por (titular, tipo) con al menos una copia no vencida —sus copias en orden de preferencia, de las que viajará
    /// UNA— y los pares que se quedan fuera por tener solo copias vencidas.
    /// </summary>
    internal sealed record Seleccion(
        IReadOnlyList<IReadOnlyList<Copia>> Enviar,
        IReadOnlyList<(Guid? TrabajadorId, Guid TipoDocumentoId)> SoloVencidos);

    /// <summary>
    /// Reglas del propietario (2026-09-20 y precisión del 2026-10-01): al Cliente empresarial se le envían los documentos
    /// vigentes, uno por (titular, tipo) y el de emisión más reciente; nunca los vencidos. El que viaja es el documento
    /// efectivo (<see cref="DocumentoEfectivo"/>), si es vigente.
    ///
    /// <para>
    /// Entre las copias vigentes de un (titular, tipo) viaja la de <c>FechaEmision</c> más reciente, aunque otra venza más
    /// tarde; el desempate es determinista y está en <see cref="DocumentoEfectivo"/>. «Sin confirmar» y
    /// <see cref="EstadoVigenciaDocumento.NoCaduca"/> (p. ej. Formación 60h) son vigentes y compiten por su emisión como
    /// cualquier otra copia.
    /// </para>
    ///
    /// <para>
    /// Vencido es <c>FechaVencimiento &lt; hoy</c> (el umbral ámbar/rojo no interviene: Próximo y Urgente siguen vigentes),
    /// evaluado con <see cref="DocumentoEfectivo.ValidoHoy"/> para no duplicar la regla. Si de un (titular, tipo) solo hay
    /// copias vencidas no se envía ninguna y el par se devuelve en <see cref="Seleccion.SoloVencidos"/>: nunca se manda el
    /// vencido «por si acaso».
    /// </para>
    ///
    /// <para>
    /// Los candidatos ya vienen filtrados a los que tienen archivo: un documento sin archivo no puede viajar y no compite por
    /// el puesto. Cada grupo conserva todas sus copias vigentes en orden de preferencia: si el archivo de la primera no se
    /// puede abrir, <see cref="ConstruirZipAsync"/> prueba la siguiente —sigue siendo un documento por titular y tipo, y
    /// nunca uno vencido—.
    /// </para>
    /// </summary>
    internal static Seleccion Seleccionar(IReadOnlyList<Candidato> candidatos, DateOnly hoy)
    {
        var enviar = new List<IReadOnlyList<Copia>>();
        var soloVencidos = new List<(Guid? TrabajadorId, Guid TipoDocumentoId)>();

        foreach (var grupo in candidatos.GroupBy(d => (d.TrabajadorId, d.TipoDocumentoId)))
        {
            // El documento efectivo del grupo (entre los que tienen archivo: si el efectivo de las pantallas no lo tiene, viaja
            // el siguiente) es el primero (DocumentoEfectivo, el mismo orden que el estado y el cumplimiento) y solo viaja si es
            // vigente. Los válidos van antes que los vencidos, así que las copias vigentes son el prefijo del orden: el efectivo
            // y, detrás, las de reserva por si su archivo no se puede abrir.
            var vigentes = DocumentoEfectivo.Ordenar(
                    grupo, d => d.EstadoVigencia, d => d.FechaVencimiento, d => d.FechaEmision, d => d.CreadoEnUtc, d => d.Id, d => d.EsRenuncia, hoy)
                .TakeWhile(d => DocumentoEfectivo.ValidoHoy(d.EstadoVigencia, d.FechaVencimiento, hoy, d.EsRenuncia))
                .ToList();

            if (vigentes.Count == 0)
            {
                soloVencidos.Add(grupo.Key);
                continue;
            }

            enviar.Add(vigentes.Select(d => new Copia(d.Id, d.TrabajadorId, d.TipoDocumentoId, d.ArchivoUrl)).ToList());
        }

        return new Seleccion(enviar, soloVencidos);
    }

    /// <summary>
    /// Devuelve null si ningún documento pudo abrirse (almacenamiento inconsistente): mejor no entregar nada que un zip
    /// vacío. De cada grupo entra UNA copia: la primera cuyo archivo se pueda abrir; si ninguna se abre, el grupo queda fuera
    /// (con un aviso por cada intento). Devuelve también qué documentos entraron de verdad: cada uno es un acceso a su
    /// contenido que quien llama registra (DEC-36). Sin hueco en el presupuesto de memoria de documentos
    /// (<see cref="PresupuestoMemoriaAgotadoException"/>) o con la operación cancelada no se prueba otra copia: el paquete
    /// entero falla, porque saltarse documentos daría por completo un paquete al que le faltan.
    /// <paramref name="ubicar"/> da la carpeta (ya saneada; la decide quien llama) y el nombre sin extensión de cada copia.
    /// <para>
    /// Opcionales, solo para el paquete de Empresa (el de la Visita no los pasa y no cambia): <paramref name="maximoBytes"/>
    /// corta con <see cref="PaqueteDocumentalDemasiadoPesadoException"/> en cuanto el zip en memoria lo supera, y
    /// <paramref name="anexoOmitidos"/> recibe, si algún documento entró, los grupos de los que no se pudo abrir ninguna copia
    /// y puede devolver un texto que se añade en la raíz del zip antes de cerrarlo.
    /// </para>
    /// </summary>
    internal static async Task<(byte[] Bytes, IReadOnlyList<(Guid DocumentoId, Guid TipoDocumentoId)> Documentos)?> ConstruirZipAsync(
        IReadOnlyList<IReadOnlyList<Copia>> grupos,
        Func<Copia, (string Carpeta, string Nombre)> ubicar,
        IFileStorageService almacenamiento,
        ILogger logger,
        CancellationToken cancellationToken,
        long? maximoBytes = null,
        Func<IReadOnlyList<IReadOnlyList<Copia>>, (string Nombre, string Contenido)?>? anexoOmitidos = null)
    {
        var omitidos = new List<IReadOnlyList<Copia>>();
        using var memoria = new MemoryStream();
        // Sin distinguir mayúsculas: quien extrae el zip lo hace en Windows, donde «ANA» y «Ana» son el mismo fichero.
        var nombresUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var agregados = new List<(Guid DocumentoId, Guid TipoDocumentoId)>();

        using (var zip = new ZipArchive(memoria, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var grupo in grupos)
            {
                var agregadosAntes = agregados.Count;
                foreach (var documento in grupo)
                {
                    Stream contenido;
                    try
                    {
                        contenido = await almacenamiento.AbrirAsync(documento.ArchivoUrl, cancellationToken);
                    }
                    catch (Exception ex) when (ex is not PresupuestoMemoriaAgotadoException and not OperationCanceledException)
                    {
                        // Solo el tipo de la excepción: su mensaje arrastra la clave del fichero.
                        logger.LogWarning("No se pudo abrir el documento {DocumentoId} para el paquete documental ({TipoExcepcion}).", documento.Id, ex.GetType().Name);
                        continue;
                    }

                    await using (contenido)
                    {
                        var (carpeta, nombre) = ubicar(documento);
                        var extension = Path.GetExtension(documento.ArchivoUrl);
                        var nombreEntrada = SanearNombreEntrada(carpeta, nombre, extension, nombresUsados);

                        var entrada = zip.CreateEntry(nombreEntrada, CompressionLevel.Fastest);
                        await using var flujoEntrada = entrada.Open();
                        await contenido.CopyToAsync(flujoEntrada, cancellationToken);
                    }

                    agregados.Add((documento.Id, documento.TipoDocumentoId));
                    if (maximoBytes is { } tope && memoria.Length > tope)
                        throw new PaqueteDocumentalDemasiadoPesadoException(tope);
                    break; // uno por titular y tipo
                }

                if (agregados.Count == agregadosAntes)
                    omitidos.Add(grupo);
            }

            if (anexoOmitidos is not null && agregados.Count > 0 && omitidos.Count > 0
                && anexoOmitidos(omitidos) is { } anexo)
            {
                // En la raíz: las copias van siempre dentro de una carpeta, así que no puede chocar con ninguna.
                var entradaAnexo = zip.CreateEntry(anexo.Nombre, CompressionLevel.Fastest);
                await using var escritor = new StreamWriter(entradaAnexo.Open(), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
                await escritor.WriteAsync(anexo.Contenido.AsMemory(), cancellationToken);
            }
        }

        return agregados.Count > 0 ? (memoria.ToArray(), agregados) : null;
    }

    /// <summary>
    /// Un segmento de ruta o de nombre de fichero hecho con datos que escribe el usuario: sin los caracteres que Windows no
    /// admite (barras incluidas, que de otro modo abrirían una subcarpeta) ni los de control, y sin puntos ni espacios al
    /// final. Para la carpeta de un Trabajador dentro del zip.
    /// </summary>
    internal static string SanearSegmento(string texto) => Sanear(texto).TrimEnd('.', ' ');

    /// <summary>
    /// Caracteres que Windows no admite en un nombre de fichero, junto con los de control (por debajo del espacio). La lista
    /// es fija a propósito: <see cref="Path.GetInvalidFileNameChars"/> depende del sistema donde corre el servidor —en Linux
    /// solo devuelve «\0» y «/»— y quien abre el zip lo hace en Windows.
    /// </summary>
    private static readonly char[] CaracteresNoValidosEnNombre = ['/', '\\', ':', '*', '?', '"', '<', '>', '|'];

    /// <summary>
    /// Nombre de una entrada del zip: «carpeta/nombre.ext». La carpeta la decide el servicio. El nombre sale de datos que
    /// escribe el usuario (Tipo de documento y titular), así que se sanea entero y se le quitan los puntos y espacios del
    /// final, para que no queden pegados a la extensión. A la extensión se le quitan también, porque Windows no admite un
    /// fichero que acabe en punto o en espacio. Si dos documentos acaban con el mismo nombre —sin distinguir mayúsculas—, el
    /// segundo lleva « (2)».
    /// </summary>
    private static string SanearNombreEntrada(string carpeta, string nombre, string extension, HashSet<string> nombresUsados)
    {
        var nombreLimpio = SanearSegmento(nombre);
        var extensionLimpia = SanearSegmento(extension);

        var candidato = $"{carpeta}/{nombreLimpio}{extensionLimpia}";
        for (var contador = 2; !nombresUsados.Add(candidato); contador++)
            candidato = $"{carpeta}/{nombreLimpio} ({contador}){extensionLimpia}";

        return candidato;
    }

    private static string Sanear(string texto) =>
        string.Concat(texto.Select(c => c < ' ' || CaracteresNoValidosEnNombre.Contains(c) ? '_' : c));
}

/// <summary>El zip en memoria pasó de su tope de bytes: no se sigue armando.</summary>
internal sealed class PaqueteDocumentalDemasiadoPesadoException(long maximoBytes)
    : Exception($"El paquete documental pasa de {maximoBytes} bytes.");

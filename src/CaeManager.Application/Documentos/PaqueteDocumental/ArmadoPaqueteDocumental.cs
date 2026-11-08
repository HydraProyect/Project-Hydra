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
    /// corta con <see cref="PaqueteDocumentalDemasiadoPesadoException"/> en cuanto un byte escrito del zip lo pasaría —también
    /// durante la copia de un Documento, el índice y el directorio central— y se vuelve a comprobar sobre el zip cerrado, y
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

        // Con tope, el zip se escribe a través de un flujo que corta en cuanto un byte lo pasaría: durante la copia de un
        // Documento, al añadir el índice y al escribir el directorio central al cerrar. Así el tope es sobre los bytes que
        // se entregan. No acota la memoria: el MemoryStream duplica su capacidad y ToArray() copia, así que el pico por petición
        // puede rondar el doble o el triple del tope.
        var conTope = maximoBytes is { } limite ? new FlujoConTope(memoria, limite) : null;
        try
        {
            await EscribirZipAsync(conTope ?? (Stream)memoria);
        }
        catch (Exception) when (conTope is { Excedido: true })
        {
            // Pasado el tope, lo que lance después el cierre de la entrada o del zip (que intentan escribir más) no tapa el
            // motivo.
            throw new PaqueteDocumentalDemasiadoPesadoException(conTope.Tope);
        }

        // El zip ya está cerrado (índice y directorio central incluidos): comprobación final sobre lo que se entrega.
        if (maximoBytes is { } tope && memoria.Length > tope)
            throw new PaqueteDocumentalDemasiadoPesadoException(tope);

        return agregados.Count > 0 ? (memoria.ToArray(), agregados) : null;

        async Task EscribirZipAsync(Stream salida)
        {
            using (var zip = new ZipArchive(salida, ZipArchiveMode.Create, leaveOpen: true))
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
        }
    }

    /// <summary>
    /// Un segmento de ruta o de nombre de fichero hecho con datos que escribe el usuario: sin los caracteres que Windows no
    /// admite (barras incluidas, que de otro modo abrirían una subcarpeta) ni los de control, y sin puntos ni espacios al
    /// final, y sin ser un nombre reservado de Windows (<see cref="EsNombreReservado"/>), que se acaba en «_». Para la carpeta
    /// de un Trabajador dentro del zip y, junto con la extensión, para el nombre de cada fichero.
    /// </summary>
    internal static string SanearSegmento(string texto)
    {
        var segmento = Sanear(texto).TrimEnd('.', ' ');
        if (!EsNombreReservado(segmento))
            return segmento;

        // El nombre reservado lo es con cualquier extensión («CON.pdf», «con.tar.gz»): el «_» va pegado al nombre base, antes del
        // primer punto, para que la extensión que se añada después ya no lo vuelva a dejar reservado.
        var punto = segmento.IndexOf('.');
        return punto < 0 ? segmento + "_" : segmento.Insert(punto, "_");
    }

    /// <summary>
    /// Nombres de dispositivo de Windows que no se pueden crear ni extraer como fichero o carpeta, con o sin extensión y sin
    /// distinguir mayúsculas: CON, PRN, AUX, NUL, COM1–COM9 y LPT1–LPT9 (también con los superíndices ¹ ² ³ que Windows
    /// trata igual). COM0 y LPT0 se tratan como reservados por prudencia: algunas versiones y herramientas de Windows los rechazan y sanearlos no cuesta nada. Se mira el nombre base —lo anterior al primer punto— sin los espacios finales, que Windows ignora.
    /// </summary>
    internal static bool EsNombreReservado(string segmento)
    {
        var punto = segmento.IndexOf('.');
        var baseDelNombre = (punto < 0 ? segmento : segmento[..punto]).TrimEnd(' ');
        if (baseDelNombre.Length == 0)
            return false;

        if (baseDelNombre.Equals("CON", StringComparison.OrdinalIgnoreCase)
            || baseDelNombre.Equals("PRN", StringComparison.OrdinalIgnoreCase)
            || baseDelNombre.Equals("AUX", StringComparison.OrdinalIgnoreCase)
            || baseDelNombre.Equals("NUL", StringComparison.OrdinalIgnoreCase))
            return true;

        return baseDelNombre.Length == 4
            && (baseDelNombre.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
                || baseDelNombre.StartsWith("LPT", StringComparison.OrdinalIgnoreCase))
            && (baseDelNombre[3] is >= '0' and <= '9' or '¹' or '²' or '³');
    }

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
        // Un nombre que se queda sin nada (solo puntos, solo espacios) sería una entrada sin nombre, o una carpeta si no hay
        // extensión: se sustituye por «_». La extensión sí puede quedar vacía.
        var nombreLimpio = SanearSegmento(nombre) is { Length: > 0 } limpio ? limpio : "_";
        var extensionLimpia = Sanear(extension).TrimEnd('.', ' ');

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

/// <summary>
/// Flujo de solo escritura sobre <paramref name="interior"/> que no deja que su longitud pase de <paramref name="tope"/>:
/// la escritura que lo superaría lanza <see cref="PaqueteDocumentalDemasiadoPesadoException"/> sin escribir nada, y desde
/// entonces todas lanzan (<see cref="Excedido"/>). Delega la posición, para que el zip pueda volver atrás a completar la
/// cabecera local de cada entrada: lo que cuenta es hasta dónde llegaría el último byte, no cuántos se escriben. No cierra
/// el interior.
/// </summary>
internal sealed class FlujoConTope(Stream interior, long tope) : Stream
{
    public long Tope => tope;

    /// <summary>Alguna escritura habría pasado del tope.</summary>
    public bool Excedido { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => interior.CanSeek;
    public override bool CanWrite => true;
    public override long Length => interior.Length;

    public override long Position
    {
        get => interior.Position;
        set => interior.Position = value;
    }

    public override void Flush() => interior.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => interior.FlushAsync(cancellationToken);
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => interior.Seek(offset, origin);

    public override void SetLength(long value)
    {
        Comprobar(value);
        interior.SetLength(value);
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Comprobar(interior.Position + buffer.Length);
        interior.Write(buffer);
    }

    public override void WriteByte(byte value)
    {
        Comprobar(interior.Position + 1);
        interior.WriteByte(value);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Comprobar(interior.Position + buffer.Length);
        return interior.WriteAsync(buffer, cancellationToken);
    }

    private void Comprobar(long finTrasEscribir)
    {
        if (!Excedido && finTrasEscribir <= tope) return;
        Excedido = true;
        throw new PaqueteDocumentalDemasiadoPesadoException(tope);
    }
}

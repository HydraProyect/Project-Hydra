using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using Microsoft.Extensions.Logging;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using SkiaSharp;

namespace CaeManager.Infrastructure.HerramientasPdf;

/// <summary>
/// Decodifica las fotos con SkiaSharp (MIT, ya presente en la solución para los sellos) y arma el PDF con PDFsharp.
///
/// Cada foto se decodifica, se orienta, se reduce si pasa del lado máximo y se REcodifica como JPEG sobre un lienzo
/// blanco. Ese paso es el que quita los metadatos: PDFsharp incrusta los bytes de un JPEG tal cual, de modo que meter el
/// original llevaría su EXIF (GPS, modelo de cámara, fecha, miniatura) dentro del PDF. El codificador de Skia solo
/// escribe lo que se le pide, y no se le pide nada.
///
/// Las dimensiones se leen de la cabecera ANTES de decodificar y se comparan con los topes de
/// <see cref="LimitesFotoAPdf"/> aunque el generador ya lo hiciera: son las que usa el decodificador, y un fichero que
/// engañe a un lector de cabeceras no tiene por qué engañar al otro. No se usa disco: todo ocurre en memoria. Los
/// mensajes de excepción no se registran, porque pueden reproducir contenido del fichero.
///
/// <para>
/// <b>Memoria.</b> El procesador reserva él mismo en el <see cref="IPresupuestoMemoriaDocumentos"/> lo que usa, sin
/// esperar (quien lo llama ya retiene las fotos leídas; esperar reteniendo bloquea a varias operaciones entre sí), y si no
/// cabe lanza <see cref="PresupuestoMemoriaAgotadoException"/>. Lo que cuesta cada foto se conoce por su cabecera
/// (decodificación y lienzo, 4 bytes por píxel de cada uno, más el búfer de coeficientes que libjpeg guarda en memoria
/// nativa para un JPEG progresivo o de varios barridos: <see cref="CabeceraJpeg.BytesDeBufferDeCoeficientes"/>, hasta
/// 6 bytes por píxel, que se reserva antes de decodificar y se devuelve cuando el decodificador ya se liberó; y, en un PNG
/// entrelazado (Adam7), el búfer con todas las filas que Skia reserva además del bitmap: <see cref="CabeceraPng.BytesDeBufferEntrelazado"/>,
/// hasta 8 bytes por píxel, con la misma regla) salvo el JPEG de salida, que NO se deduce del tamaño de la foto
/// subida (un PNG de 170 KB con un patrón repetido da un JPEG de varios MB): se reserva por trozos a medida que el
/// codificador lo escribe, y si el presupuesto se agota se descarta lo que falta y se falla al terminar. Del PDF se
/// reservan la copia de cada JPEG que retiene el documento (<see cref="LimitesFotoAPdf.CopiasDelJpegEnElDocumento"/>) y el
/// flujo de salida, que se entrega tal cual a la pantalla sin copiarlo a otro array. El dibujo reducido usa muestreo sin
/// mipmaps (<see cref="MuestreoDeReduccion"/>): la pirámide que Skia cachea con ellos no entraría en la cuenta.
/// </para>
///
/// <para>
/// <b>Cancelación.</b> Skia decodifica en una sola llamada nativa que no mira ningún token (y su decodificación por
/// líneas no existe para PNG en esta versión: devuelve «no implementado»), pero lee los bytes de la foto por el flujo que
/// se le da. Ese flujo (<see cref="FlujoDeEntrada"/>) devuelve fin de datos en cuanto el token se cancela: el decodificador
/// lo toma por una entrada truncada y termina a los pocos KB, y el procesador lanza entonces
/// <see cref="OperationCanceledException"/>. Además el token se comprueba entre etapas (decodificar, dibujar, codificar).
/// Lo que sigue sin poder interrumpirse son las llamadas nativas que ya no leen entrada: dibujar la imagen reducida y
/// codificar el JPEG; están acotadas por el tope de píxeles y duran una fracción de segundo. Guardar el PDF tampoco se
/// interrumpe, pero el token se comprueba al terminar y un PDF cuyo guardado coincidió con la cancelación no se entrega.
/// </para>
/// </summary>
public sealed class SkiaPdfSharpProcesadorFotosPdf(
    ILogger<SkiaPdfSharpProcesadorFotosPdf> logger, IPresupuestoMemoriaDocumentos presupuesto) : IProcesadorFotosPdf
{
    private const int CalidadJpeg = 90;

    /// <summary>Filtro lineal SIN mipmaps: la pirámide que Skia crea y cachea con ellos no entra en ninguna reserva.</summary>
    internal static readonly SKSamplingOptions MuestreoDeReduccion = new(SKFilterMode.Linear, SKMipmapMode.None);

    /// <summary>5 mm de margen en cada lado de la página, para que una impresora doméstica no recorte la foto.</summary>
    private const double MargenPuntos = 14.17;

    /// <summary>Lo que el PDF añade a los JPEG que incrusta: cabecera, catálogo, tabla de referencias y diccionarios de página.</summary>
    private const long SobrecosteFijoPdfBytes = 64 * 1024;

    private const long SobrecostePorPaginaBytes = 8 * 1024;

    /// <summary>
    /// Solo para los tests: se llama con el nombre de cada etapa que termina («decodificada», «dibujada», «codificada»,
    /// «guardado» al volver <c>PdfDocument.Save</c>) y
    /// con «lectura» cada vez que el decodificador pide bytes de la foto, de modo que un test pueda cancelar en una
    /// frontera concreta y comprobar que no se pasa de ella.
    /// </summary>
    internal Action<string>? AlTerminarEtapa { get; set; }

    /// <summary>Solo para los tests: se llama cada vez que PDFsharp escribe en el flujo de salida, es decir, DURANTE el guardado.</summary>
    internal Action? AlEscribirElPdf { get; set; }

    /// <summary>El flujo de salida del PDF, que avisa de cada escritura para que un test pueda cancelar a mitad del guardado.</summary>
    private sealed class SalidaObservable(int capacidad, Action? alEscribir) : MemoryStream(capacidad)
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            alEscribir?.Invoke();
            base.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            alEscribir?.Invoke();
            base.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            alEscribir?.Invoke();
            base.WriteByte(value);
        }
    }

    public Result<PdfDeFotos> ComponerPdf(IReadOnlyList<byte[]> fotos, CancellationToken cancellationToken)
    {
        if (fotos.Count == 0)
            return FalloPdf(ErroresFotoAPdf.SinFotos, "No hay fotos.");

        var cuentaPdf = new Cuenta(presupuesto);
        MemoryStream? salida = null;
        var entregado = false;
        try
        {
            long totalJpeg = 0;
            using (var cuentaDocumento = new Cuenta(presupuesto))
            using (var documento = new PdfDocument())
            {
                for (var i = 0; i < fotos.Count; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // El JPEG de cada foto queda reservado en cuentaPdf: es la parte que ocupará en el PDF de salida.
                    var normalizada = Normalizar(fotos[i], LimitesFotoAPdf.LadoMaximoEnPdfPixeles, cuentaPdf, cancellationToken);
                    if (normalizada.EsFallido)
                        return FalloPdf(normalizada.Error.Codigo, $"Foto {i + 1}: {normalizada.Error.Mensaje}");

                    var longitud = normalizada.Valor.Jpeg.LongLength;
                    cuentaDocumento.Reservar(LimitesFotoAPdf.CopiasDelJpegEnElDocumento * longitud);
                    AnadirPagina(documento, normalizada.Valor);
                    totalJpeg += longitud;
                }

                cancellationToken.ThrowIfCancellationRequested();

                var capacidad = totalJpeg + SobrecosteFijoPdfBytes + SobrecostePorPaginaBytes * fotos.Count;
                cuentaPdf.Reservar(capacidad - totalJpeg);
                salida = new SalidaObservable(checked((int)capacidad), AlEscribirElPdf);
                documento.Save(salida, closeStream: false);

                // Guardar es una sola llamada de PDFsharp que no mira el token: si se canceló mientras tanto, el PDF no se
                // entrega. El finally libera el flujo y la reserva; sin esto el éxito prolongaría el trabajo y la retención
                // de un PDF que nadie va a descargar.
                AlTerminarEtapa?.Invoke("guardado");
                cancellationToken.ThrowIfCancellationRequested();

                // Si el PDF pasó de lo previsto, el flujo creció: se cuenta lo que ocupa de verdad.
                if (salida.Capacity > capacidad)
                    cuentaPdf.Reservar(salida.Capacity - capacidad);
            }

            salida.Position = 0;
            entregado = true;
            return Result.Exito(new PdfDeFotos(salida, cuentaPdf));
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or PresupuestoMemoriaAgotadoException))
        {
            // Componer la página y guardar el PDF son de PDFsharp, que lanza excepciones de varios tipos: ninguna debe
            // cruzar al circuito. Solo el tipo, nunca el contenido de la foto.
            logger.LogInformation("No se pudo componer el PDF de fotos ({Tipo}).", ex.GetType().Name);
            return FalloPdf(ErroresFotoAPdf.NoSePudoGenerar, "No hemos podido crear el PDF.");
        }
        finally
        {
            if (!entregado)
            {
                salida?.Dispose();
                cuentaPdf.Dispose();
            }
        }
    }

    public Result<byte[]> CrearMiniatura(byte[] foto, int ladoMaximoPixeles, CancellationToken cancellationToken)
    {
        var normalizada = Normalizar(foto, ladoMaximoPixeles, cancellationToken);
        return normalizada.EsFallido ? Result.Fallo<byte[]>(normalizada.Error) : Result.Exito(normalizada.Valor.Jpeg);
    }

    /// <summary>Foto ya orientada, reducida y sin metadatos.</summary>
    public readonly record struct FotoNormalizada(byte[] Jpeg, int Ancho, int Alto);

    /// <summary>
    /// Decodifica, aplica la orientación EXIF a los píxeles, reduce al lado máximo (nunca amplía) y recodifica como JPEG.
    /// Público para que un test compruebe píxeles y metadatos sin pasar por el PDF. Reserva lo que usa y lo devuelve al
    /// volver: el JPEG que entrega ya no está contado (<see cref="ComponerPdf"/> sí lo cuenta).
    /// </summary>
    public Result<FotoNormalizada> Normalizar(byte[] foto, int ladoMaximoPixeles, CancellationToken cancellationToken = default)
    {
        using var cuentaSalida = new Cuenta(presupuesto);
        return Normalizar(foto, ladoMaximoPixeles, cuentaSalida, cancellationToken);
    }

    /// <summary>La reserva del JPEG resultante queda en <paramref name="cuentaSalida"/>: la devuelve quien la creó.</summary>
    private Result<FotoNormalizada> Normalizar(byte[] foto, int ladoMaximoPixeles, Cuenta cuentaSalida, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Declarada ANTES que el decodificador: se desecha DESPUÉS que él, así que su reserva no vuelve al presupuesto
            // mientras el decodificador siga reteniendo memoria nativa (el búfer de coeficientes de un JPEG progresivo).
            using var cuentaDecodificacion = new Cuenta(presupuesto);

            // Sobre los bytes tal cual, sin la copia que haría SKData.CreateCopy.
            using var origen = new SKManagedStream(
                new FlujoDeEntrada(new MemoryStream(foto, writable: false), cancellationToken, () => AlTerminarEtapa?.Invoke("lectura")),
                disposeManagedStream: true);
            using var codec = SKCodec.Create(origen);
            cancellationToken.ThrowIfCancellationRequested();
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Png))
                return FalloNormalizacion(ErroresFotoAPdf.FormatoNoSoportado, "Solo se admiten fotos JPEG y PNG.");

            var info = codec.Info;
            if (info.Width <= 0 || info.Height <= 0
                || info.Width > LimitesFotoAPdf.MaximoLadoPixeles || info.Height > LimitesFotoAPdf.MaximoLadoPixeles
                || (long)info.Width * info.Height > LimitesFotoAPdf.MaximoPixeles)
                return FalloNormalizacion(ErroresFotoAPdf.DemasiadosPixeles, "La foto declara más píxeles de los admitidos.");

            var origenExif = codec.EncodedOrigin;
            var escala = Math.Min(1.0, (double)Math.Max(1, ladoMaximoPixeles) / Math.Max(info.Width, info.Height));
            var ancho = Math.Max(1, (int)Math.Round(info.Width * escala));
            var alto = Math.Max(1, (int)Math.Round(info.Height * escala));
            var girada = EsGiroDeCuartoDeVuelta(origenExif);
            var anchoSalida = girada ? alto : ancho;
            var altoSalida = girada ? ancho : alto;

            // Lo que cuestan decodificar y dibujar se sabe por la cabecera: se reserva ANTES de pedir esa memoria. A los píxeles
            // decodificados se suma, en un JPEG progresivo o de varios barridos, el búfer de coeficientes que libjpeg guarda
            // en memoria nativa (hasta 6 bytes por píxel): no es un bitmap de Skia, pero ocupa el proceso igual.
            // A los de un PNG entrelazado (Adam7), el búfer nativo con todas las filas que Skia reserva además del bitmap.
            long bytesNativosAdicionales;
            if (codec.EncodedFormat == SKEncodedImageFormat.Jpeg)
            {
                // Sin una cabecera legible no hay forma de presupuestar la decodificación: se rechaza.
                if (CabeceraJpeg.Leer(foto) is not { } cabecera)
                    return FalloNormalizacion(ErroresFotoAPdf.FotoIlegible, "No hemos podido leer esta foto.");
                bytesNativosAdicionales = cabecera.BytesDeBufferDeCoeficientes;
            }
            else
            {
                if (CabeceraPng.Leer(foto) is not { } cabeceraPng)
                    return FalloNormalizacion(ErroresFotoAPdf.FotoIlegible, "No hemos podido leer esta foto.");
                bytesNativosAdicionales = cabeceraPng.BytesDeBufferEntrelazado;
            }

            using var cuentaLienzo = new Cuenta(presupuesto);
            cuentaDecodificacion.Reservar((long)info.Width * info.Height * LimitesFotoAPdf.BytesPorPixelDecodificado + bytesNativosAdicionales);
            cuentaLienzo.Reservar((long)anchoSalida * altoSalida * LimitesFotoAPdf.BytesPorPixelDecodificado);

            using var destino = new SKBitmap(new SKImageInfo(anchoSalida, altoSalida, SKColorType.Rgba8888, SKAlphaType.Opaque));
            using (var bitmap = SKBitmap.Decode(codec, new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Premul)))
            {
                // Si se canceló durante la decodificación, el flujo cortó la entrada y lo decodificado es parcial: se descarta.
                cancellationToken.ThrowIfCancellationRequested();
                if (bitmap is null)
                    return FalloNormalizacion(ErroresFotoAPdf.FotoIlegible, "No hemos podido leer esta foto.");

                AlTerminarEtapa?.Invoke("decodificada");
                cancellationToken.ThrowIfCancellationRequested();

                using var lienzo = new SKCanvas(destino);
                lienzo.Clear(SKColors.White);
                lienzo.SetMatrix(MatrizDeOrientacion(origenExif, info.Width, info.Height, escala));
                // Inmutable: SKImage.FromBitmap comparte los píxeles en vez de copiarlos (la reserva cuenta una sola copia).
                bitmap.SetImmutable();
                using var imagen = SKImage.FromBitmap(bitmap);
                // Sin mipmaps (SKMipmapMode.None): con ellos Skia construye y cachea una pirámide de niveles (un tercio más que el
                // bitmap) que la reserva no cuenta. El filtro lineal sin pirámide alisa menos una reducción muy grande (una miniatura
                // de 128 px sale de una foto de hasta 12000 px), pero es una vista previa; en el PDF la reducción máxima es de 3,4 veces.
                lienzo.DrawImage(imagen, 0, 0, MuestreoDeReduccion);
            }

            // La decodificación ya no existe: se libera el decodificador (y con él su búfer de coeficientes) y SOLO entonces
            // vuelve su reserva, antes de codificar.
            codec.Dispose();
            cuentaDecodificacion.Dispose();
            AlTerminarEtapa?.Invoke("dibujada");
            cancellationToken.ThrowIfCancellationRequested();

            using var cuentaCodificacion = new Cuenta(presupuesto);
            using var flujo = new FlujoJpegContado(cuentaCodificacion);
            using (var destinoNativo = new SKManagedWStream(flujo, disposeManagedStream: false))
            using (var pixeles = destino.PeekPixels())
            {
                var opciones = new SKJpegEncoderOptions(CalidadJpeg, SKJpegEncoderDownsample.Downsample444, SKJpegEncoderAlphaOption.Ignore);
                if (!pixeles.Encode(destinoNativo, opciones))
                    return FalloNormalizacion(ErroresFotoAPdf.NoSePudoGenerar, "No hemos podido procesar esta foto.");
            }

            AlTerminarEtapa?.Invoke("codificada");
            cancellationToken.ThrowIfCancellationRequested();

            // El presupuesto se agotó mientras se codificaba: lo que faltaba se descartó y el JPEG no está completo.
            if (flujo.Agotado)
                throw new PresupuestoMemoriaAgotadoException();

            // Reservado el array exacto mientras los trozos siguen contados: nunca hay memoria sin reserva.
            cuentaSalida.Reservar(flujo.Longitud);
            return Result.Exito(new FotoNormalizada(flujo.ToArray(), anchoSalida, altoSalida));
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException or PresupuestoMemoriaAgotadoException))
        {
            logger.LogInformation("Foto no procesable para el PDF ({Tipo}).", ex.GetType().Name);
            return FalloNormalizacion(ErroresFotoAPdf.FotoIlegible, "No hemos podido leer esta foto.");
        }
    }

    private static bool EsGiroDeCuartoDeVuelta(SKEncodedOrigin origen) =>
        origen is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;

    /// <summary>
    /// Lleva un píxel (x, y) de la foto tal como se guardó a su sitio en la imagen derecha, ya con la escala. Los ocho
    /// valores son los de la etiqueta EXIF Orientation: 1 normal, 2 espejo horizontal, 3 giro de 180°, 4 espejo vertical,
    /// 5 transpuesta, 6 giro de 90° a la derecha, 7 transversal y 8 giro de 90° a la izquierda. Fuera de ellos, no se gira.
    /// </summary>
    private static SKMatrix MatrizDeOrientacion(SKEncodedOrigin origen, int ancho, int alto, double escala)
    {
        // destinoX = a*x + c*y + tx; destinoY = b*x + d*y + ty
        var (a, b, c, d, tx, ty) = origen switch
        {
            SKEncodedOrigin.TopRight => (-1, 0, 0, 1, ancho, 0),
            SKEncodedOrigin.BottomRight => (-1, 0, 0, -1, ancho, alto),
            SKEncodedOrigin.BottomLeft => (1, 0, 0, -1, 0, alto),
            SKEncodedOrigin.LeftTop => (0, 1, 1, 0, 0, 0),
            SKEncodedOrigin.RightTop => (0, 1, -1, 0, alto, 0),
            SKEncodedOrigin.RightBottom => (0, -1, -1, 0, alto, ancho),
            SKEncodedOrigin.LeftBottom => (0, -1, 1, 0, 0, ancho),
            _ => (1, 0, 0, 1, 0, 0),
        };

        var s = (float)escala;
        return new SKMatrix
        {
            ScaleX = a * s,
            SkewY = b * s,
            SkewX = c * s,
            ScaleY = d * s,
            TransX = tx * s,
            TransY = ty * s,
            Persp2 = 1,
        };
    }

    /// <summary>Página A4, derecha o apaisada según la foto, con la foto entera y centrada, sin recortar ni deformar.</summary>
    private static void AnadirPagina(PdfDocument documento, FotoNormalizada foto)
    {
        var pagina = documento.AddPage();
        pagina.Size = PdfSharp.PageSize.A4;
        pagina.Orientation = foto.Ancho > foto.Alto ? PdfSharp.PageOrientation.Landscape : PdfSharp.PageOrientation.Portrait;

        using var graficos = XGraphics.FromPdfPage(pagina);
        using var flujo = new MemoryStream(foto.Jpeg, writable: false);
        using var imagen = XImage.FromStream(flujo);

        var anchoDisponible = pagina.Width.Point - 2 * MargenPuntos;
        var altoDisponible = pagina.Height.Point - 2 * MargenPuntos;
        var factor = Math.Min(anchoDisponible / foto.Ancho, altoDisponible / foto.Alto);
        var anchoFinal = foto.Ancho * factor;
        var altoFinal = foto.Alto * factor;

        graficos.DrawImage(imagen,
            MargenPuntos + (anchoDisponible - anchoFinal) / 2,
            MargenPuntos + (altoDisponible - altoFinal) / 2,
            anchoFinal, altoFinal);
    }

    private static Result<PdfDeFotos> FalloPdf(string codigo, string mensaje) => Result.Fallo<PdfDeFotos>(Error.Crear(codigo, mensaje));

    private static Result<FotoNormalizada> FalloNormalizacion(string codigo, string mensaje) =>
        Result.Fallo<FotoNormalizada>(Error.Crear(codigo, mensaje));

    /// <summary>Reservas del presupuesto de memoria que se devuelven juntas. Sin esperar: si no cabe, falla cerrado.</summary>
    internal sealed class Cuenta(IPresupuestoMemoriaDocumentos presupuesto) : IDisposable
    {
        private readonly List<ReservaMemoriaDocumentos> _reservas = [];

        public bool TryReservar(long bytes)
        {
            if (bytes <= 0)
                return true;
            if (!presupuesto.TryReservar(bytes, out var reserva))
                return false;
            _reservas.Add(reserva);
            return true;
        }

        public void Reservar(long bytes)
        {
            if (!TryReservar(bytes))
                throw new PresupuestoMemoriaAgotadoException();
        }

        public void Dispose()
        {
            foreach (var reserva in _reservas)
                reserva.Dispose();
            _reservas.Clear();
        }
    }

    /// <summary>
    /// Flujo de escritura del codificador que guarda el JPEG en trozos y reserva cada trozo ANTES de pedirlo. Si no cabe,
    /// deja de guardar y se marca <see cref="Agotado"/>: una excepción dentro de la llamada nativa del codificador no es
    /// una opción, así que el fallo se declara al volver.
    /// </summary>
    internal sealed class FlujoJpegContado(Cuenta cuenta) : Stream
    {
        public const int Trozo = 256 * 1024;

        private readonly List<byte[]> _trozos = [];
        private int _usadoEnElUltimo;

        public long Longitud { get; private set; }

        public bool Agotado { get; private set; }

        public byte[] ToArray()
        {
            var resultado = new byte[Longitud];
            var posicion = 0;
            foreach (var trozo in _trozos)
            {
                var n = (int)Math.Min(trozo.Length, Longitud - posicion);
                Array.Copy(trozo, 0, resultado, posicion, n);
                posicion += n;
            }

            return resultado;
        }

        public override void Write(ReadOnlySpan<byte> datos)
        {
            while (!datos.IsEmpty && !Agotado)
            {
                if (_trozos.Count == 0 || _usadoEnElUltimo == Trozo)
                {
                    if (!cuenta.TryReservar(Trozo))
                    {
                        Agotado = true;
                        return;
                    }

                    _trozos.Add(new byte[Trozo]);
                    _usadoEnElUltimo = 0;
                }

                var n = Math.Min(Trozo - _usadoEnElUltimo, datos.Length);
                datos[..n].CopyTo(_trozos[^1].AsSpan(_usadoEnElUltimo));
                _usadoEnElUltimo += n;
                Longitud += n;
                datos = datos[n..];
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => Longitud;

        public override long Position
        {
            get => Longitud;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Los bytes de la foto tal como los lee Skia, con la cancelación incorporada: en cuanto el token se cancela devuelve
    /// fin de datos, y el decodificador (que no mira ningún token) cree que la entrada terminó y sale a los pocos KB. No
    /// lanza: una excepción dentro de la llamada nativa no es una opción.
    /// </summary>
    internal sealed class FlujoDeEntrada(Stream interno, CancellationToken cancellationToken, Action? alLeer = null) : Stream
    {
        public override int Read(Span<byte> destino)
        {
            if (cancellationToken.IsCancellationRequested)
                return 0;

            var leidos = interno.Read(destino);
            alLeer?.Invoke();
            return leidos;
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override bool CanRead => true;
        public override bool CanSeek => interno.CanSeek;
        public override bool CanWrite => false;
        public override long Length => interno.Length;

        public override long Position
        {
            get => interno.Position;
            set => interno.Position = value;
        }

        public override long Seek(long offset, SeekOrigin origin) => interno.Seek(offset, origin);

        public override void Flush()
        {
        }

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                interno.Dispose();
            base.Dispose(disposing);
        }
    }
}

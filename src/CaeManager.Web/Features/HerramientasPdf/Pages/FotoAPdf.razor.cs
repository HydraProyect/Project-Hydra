using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.HerramientasPdf.Recursos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.HerramientasPdf.Pages;

/// <summary>
/// Foto a PDF: subir fotos del móvil, ordenarlas y descargar un PDF con una página por foto. No toca ningún Documento
/// almacenado (solo lo que sube quien la usa) y el resultado no se guarda: se entrega al navegador y desaparece.
///
/// Las fotos esperan en el almacén temporal del circuito (el mismo de Dividir un PDF, con su cuota y su borrado al
/// salir), no en memoria. Cada una se comprueba al añadirla por su firma de bytes y por las dimensiones de su cabecera,
/// ANTES de decodificarla; al generar, el generador lo repite. Las miniaturas salen del mismo procesador que el PDF, así que
/// ya van giradas según el EXIF. La miniatura de cada foto vive en el circuito como la cadena data: con la que se pinta
/// (se convierte una sola vez, no en cada render), con el JPEG acotado por
/// <see cref="LimitesFotoAPdf.TamanoMaximoMiniaturaBytes"/>, y reserva en el presupuesto de memoria esa cadena (UTF-16) y
/// su copia en el lote de render pendiente de confirmar (<see cref="BytesReservadosPorCaracter"/>) hasta que el navegador
/// confirma el render que la quita, o hasta desechar la pantalla.
///
/// <para>
/// El trabajo de CPU (miniaturas y composición) pasa por <see cref="LimitadorConversionesPdf"/>, un límite del proceso.
/// El rol se comprueba al abrir el circuito (<c>[Authorize]</c> de la página), como en Dividir un PDF: no se revalida en cada
/// operación.
/// </para>
/// </summary>
public partial class FotoAPdf : CaeManager.Web.Components.PaginaInteractiva, IAsyncDisposable
{
    private const int LadoMiniaturaPixeles = 128;

    /// <summary>
    /// Una foto ya aceptada: su fichero temporal, lo que declara su cabecera y su vista previa (bytes JPEG, acotados por
    /// <see cref="LimitesFotoAPdf.TamanoMaximoMiniaturaBytes"/>), con la reserva del presupuesto de memoria que la cubre
    /// mientras viva en el circuito.
    /// </summary>
    private sealed record FotoElegida(
        ArchivoTemporal Archivo, string Nombre, long Bytes, FotoValidada Datos, string? UrlMiniatura, IDisposable? ReservaMiniatura);

    private const string PrefijoUrlMiniatura = "data:image/jpeg;base64,";

    /// <summary>
    /// Por cada carácter de la cadena data: de una miniatura se reservan 4 bytes, que es el pico real al crearla:
    /// <c>Convert.ToBase64String</c> devuelve una cadena UTF-16 (2 bytes por carácter) y concatenarla con el prefijo crea
    /// otra (otros 2), de modo que durante la concatenación coexisten 4 bytes por carácter, además del array JPEG que ya
    /// retiene el procesador (acotado por <see cref="LimitesFotoAPdf.TamanoMaximoMiniaturaBytes"/>). Ya creada, lo que queda
    /// vivo son 3: la cadena retenida (2) y su copia en el lote de render pendiente de confirmar (1, UTF-8: el Base64 es
    /// ASCII); la reserva se mantiene en 4 hasta quitar la foto, del lado seguro. Cada cadena viaja en UN solo lote: las filas
    /// llevan <c>@key</c>, así que mover o quitar fotos permuta o retira filas sin volver a enviar el <c>src</c> de las demás.
    /// </summary>
    internal const int BytesReservadosPorCaracter = 4;

    /// <summary>Se lanza cuando la miniatura no encuentra hueco en <see cref="LimitadorConversionesPdf"/> a tiempo.</summary>
    private sealed class SinHuecoDeConversionException : Exception;

    private readonly List<(int PintadoAlQuitar, IDisposable Reserva)> _reservasPorSoltar = [];
    private int _pintadosEmpezados = 1;
    private int _pintadosConfirmados;

    [Inject] private LimitadorConversionesPdf Limitador { get; set; } = default!;
    [Inject] private GeneradorPdfDesdeFotos Generador { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private CuotaSubidaMasiva CuotaSubidaMasiva { get; set; } = default!;
    [Inject] private IPresupuestoMemoriaDocumentos PresupuestoMemoria { get; set; } = default!;
    [Inject] private IProcesadorFotosPdf Procesador { get; set; } = default!;
    [Inject] private IDisponibilidadHerramientasPdf Disponibilidad { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private ILogger<FotoAPdf> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosHerramientasPdf> Textos { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private readonly List<FotoElegida> _fotos = [];

    private bool HayHuecoParaMasFotos => _fotos.Count < LimitesFotoAPdf.MaximoFotos;
    private readonly List<string> _rechazadas = [];

    private bool _inicializando = true;
    private bool _disponible;
    private bool _cargando;
    private bool _generando;
    private bool _desechado;
    private string? _error;
    private AlmacenTemporalSubidaMasiva? _almacen;
    private IJSObjectReference? _modulo;

    protected override async Task OnInitializedAsync()
    {
        _disponible = await Disponibilidad.EstaDisponibleAsync(_ciclo.Token);
        if (_disponible)
        {
            // Por la puerta: la página se inicializa a la vez que el layout, que comparte el DbContext.
            var tenantOrigenId = await PuertaAccesoDatos.EjecutarAsync(() => CurrentUserService.ObtenerTenantOrigenIdAsync());
            if (!_desechado)
                _almacen = CuotaSubidaMasiva.CrearAlmacen(tenantOrigenId);
        }

        _inicializando = false;
    }

    private async Task AnadirFotosAsync(InputFileChangeEventArgs evento)
    {
        if (_cargando || _generando || _almacen is not { } almacen)
            return;

        _error = null;
        _rechazadas.Clear();

        IReadOnlyList<IBrowserFile> archivos;
        try
        {
            archivos = evento.GetMultipleFiles(LimitesFotoAPdf.MaximoFotos);
        }
        catch (InvalidOperationException)
        {
            _error = Textos["FotoErrorDemasiadas", LimitesFotoAPdf.MaximoFotos];
            return;
        }

        _cargando = true;
        await InvokeAsync(StateHasChanged);
        var token = _ciclo.Token;
        try
        {
            foreach (var archivo in archivos)
            {
                if (_fotos.Count >= LimitesFotoAPdf.MaximoFotos)
                {
                    _rechazadas.Add($"{archivo.Name}: {Textos["FotoErrorDemasiadas", LimitesFotoAPdf.MaximoFotos]}");
                    continue;
                }

                await AnadirUnaAsync(almacen, archivo, token);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (Exception ex) when (ex is CuotaSubidaMasivaSuperadaException or PresupuestoMemoriaAgotadoException)
        {
            _error = Textos["ErrorSinEspacio"];
        }
        catch (SinHuecoDeConversionException)
        {
            _error = MensajeDe(ErroresFotoAPdf.Ocupado);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task AnadirUnaAsync(AlmacenTemporalSubidaMasiva almacen, IBrowserFile archivo, CancellationToken token)
    {
        var nombre = Path.GetFileName(archivo.Name);

        // El tamaño declarado se comprueba ANTES de leer ni un byte; el almacén reserva cuota y corta si llega más.
        if (archivo.Size > LimitesFotoAPdf.TamanoMaximoFotoBytes)
        {
            _rechazadas.Add($"{nombre}: {MensajeDe(ErroresFotoAPdf.FotoDemasiadoGrande)}");
            return;
        }

        if (_fotos.Sum(f => f.Bytes) + archivo.Size > LimitesFotoAPdf.TamanoMaximoTotalBytes)
        {
            _rechazadas.Add($"{nombre}: {MensajeDe(ErroresFotoAPdf.TotalDemasiadoGrande)}");
            return;
        }

        ArchivoTemporal? guardado = null;
        try
        {
            guardado = await almacen.GuardarAsync(
                () => archivo.OpenReadStream(LimitesFotoAPdf.TamanoMaximoFotoBytes, token),
                archivo.Size, LimitesFotoAPdf.TamanoMaximoFotoBytes, cancellationToken: token);

            FotoValidada? datos = null;
            byte[]? bytesMiniatura = null;
            string? codigoRechazo = null;
            using (await PresupuestoMemoria.ReservarAsync(guardado.TamanoBytes, token))
            {
                var contenido = await almacen.LeerAsync(guardado, token);
                var validada = ValidadorFotoAPdf.Validar(contenido);
                if (validada.EsFallido)
                {
                    codigoRechazo = validada.Error.Codigo;
                }
                else
                {
                    // La miniatura decodifica la foto entera: el procesador reserva lo que cuesta (decodificación, lienzo y el
                    // JPEG que produce) sin esperar, porque esta operación ya retiene la reserva del fichero leído, y lanza
                    // PresupuestoMemoriaAgotadoException si no cabe. Cancelar interrumpe la decodificación entre bandas.
                    // Es trabajo de CPU: entra por el limitador del proceso, como la composición del PDF. Sin hueco a tiempo se
                    // corta la selección entera (las siguientes esperarían lo mismo) y se avisa de que se reintente.
                    Result<byte[]> miniatura;
                    using (var turno = await Limitador.EntrarAsync(token))
                    {
                        if (turno is null)
                            throw new SinHuecoDeConversionException();
                        miniatura = await Task.Run(() => Procesador.CrearMiniatura(contenido, LadoMiniaturaPixeles, token), token);
                    }
                    // Si el procesador no puede leer la foto (la cabecera es válida pero no decodifica), la misma foto fallaría al
                    // generar el PDF: se rechaza con su motivo, como cualquier otra, sin tocar las demás de la selección.
                    if (miniatura.EsFallido)
                    {
                        codigoRechazo = miniatura.Error.Codigo;
                    }
                    else
                    {
                        datos = validada.Valor;
                        bytesMiniatura = miniatura.Valor;
                    }
                }
            }

            if (datos is not { } datosFoto)
            {
                almacen.Eliminar(guardado);
                _rechazadas.Add($"{nombre}: {MensajeDe(codigoRechazo ?? ErroresFotoAPdf.FotoIlegible)}");
                return;
            }

            // La miniatura vive en el circuito hasta quitar la foto o desechar la pantalla, y la reserva del procesador terminó
            // al devolverla. Lo que se retiene es la cadena data: con el Base64, y se reserva ANTES de crearla, con su tamaño
            // calculado desde los bytes del JPEG (BytesReservadosDeMiniatura), sin esperar, para no esperar a una reserva
            // ajena desde un circuito. Se devuelve con la foto, cuando el navegador ya confirmó el render que la quita. Una
            // miniatura que pasa del tope no se retiene: la foto entra sin vista previa.
            IDisposable? reservaMiniatura = null;
            var reservaEntregada = false;
            try
            {
                string? urlMiniatura = null;
                if (bytesMiniatura is { Length: > 0 } && bytesMiniatura.Length <= LimitesFotoAPdf.TamanoMaximoMiniaturaBytes)
                {
                    if (!PresupuestoMemoria.TryReservar(BytesReservadosDeMiniatura(bytesMiniatura.Length), out var reserva))
                        throw new PresupuestoMemoriaAgotadoException();
                    reservaMiniatura = reserva;
                    urlMiniatura = CrearUrlMiniatura(bytesMiniatura);
                }

                // La pantalla se desechó mientras se procesaba: nadie liberaría esta foto; su reserva la suelta el finally.
                if (_desechado)
                {
                    almacen.Eliminar(guardado);
                    return;
                }

                _fotos.Add(new FotoElegida(guardado, nombre, guardado.TamanoBytes, datosFoto, urlMiniatura, reservaMiniatura));
                reservaEntregada = true;
            }
            finally
            {
                // Hasta que la foto la posee (_fotos), la reserva es de esta llamada: si crear la cadena o añadir la foto lanza,
                // o la pantalla se desechó, se libera aquí, una sola vez (una vez entregada, la libera la foto al quitarse).
                if (!reservaEntregada)
                    reservaMiniatura?.Dispose();
            }
        }
        catch (ArchivoTemporalDemasiadoGrandeException)
        {
            _rechazadas.Add($"{nombre}: {MensajeDe(ErroresFotoAPdf.FotoDemasiadoGrande)}");
        }
        catch (IOException)
        {
            // La lectura de la subida se cortó (el navegador cerró la conexión o el fichero cambió): esa foto no entra,
            // pero las que ya estaban aceptadas y las demás de la selección siguen su curso.
            if (guardado is not null && _fotos.TrueForAll(f => f.Archivo != guardado))
                almacen.Eliminar(guardado);
            _rechazadas.Add($"{nombre}: {MensajeDe(ErroresFotoAPdf.FotoIlegible)}");
        }
        catch
        {
            // Cualquier otra salida (cuota, presupuesto, cancelación): la foto a medias no se queda en disco.
            if (guardado is not null && _fotos.TrueForAll(f => f.Archivo != guardado))
                almacen.Eliminar(guardado);
            throw;
        }
    }

    private void Mover(int indice, int salto)
    {
        var destino = indice + salto;
        if (_generando || indice < 0 || indice >= _fotos.Count || destino < 0 || destino >= _fotos.Count)
            return;

        (_fotos[indice], _fotos[destino]) = (_fotos[destino], _fotos[indice]);
    }

    private void Quitar(FotoElegida foto)
    {
        if (_generando || !_fotos.Remove(foto))
            return;

        _almacen?.Eliminar(foto.Archivo);
        AplazarHastaElSiguientePintado(foto.ReservaMiniatura);
        _error = null;
        _rechazadas.Clear();
    }

    private void QuitarTodas()
    {
        if (_generando)
            return;

        foreach (var foto in _fotos)
        {
            _almacen?.Eliminar(foto.Archivo);
            AplazarHastaElSiguientePintado(foto.ReservaMiniatura);
        }

        _fotos.Clear();
        _error = null;
        _rechazadas.Clear();
    }

    private async Task DescargarAsync()
    {
        if (_generando || _cargando || _fotos.Count == 0 || _almacen is not { } almacen)
            return;

        _generando = true;
        _error = null;
        _rechazadas.Clear();
        await InvokeAsync(StateHasChanged);
        var token = _ciclo.Token;
        try
        {
            // Una copia: la reserva y la lectura se calculan sobre las mismas fotos aunque la lista cambie mientras se espera.
            var elegidas = _fotos.ToList();
            var bytes = elegidas.Sum(f => f.Bytes);

            // Las fotos leídas, que se retienen mientras se compone. Lo demás (decodificar, el JPEG recodificado, lo que
            // retiene el documento y el PDF de salida) lo reserva el procesador a medida que lo produce: el tamaño del
            // JPEG no se deduce de los bytes subidos ni de los píxeles, así que no se puede presupuestar de antemano.
            using var reserva = await PresupuestoMemoria.ReservarAsync(bytes, token);

            var contenidos = new List<byte[]>(elegidas.Count);
            foreach (var foto in elegidas)
                contenidos.Add(await almacen.LeerAsync(foto.Archivo, token));

            var resultado = await Generador.GenerarAsync(contenidos, token);
            if (resultado.EsFallido)
            {
                _error = MensajeDe(resultado.Error.Codigo);
                return;
            }

            // El PDF no se guarda: va al navegador como flujo, sin copiarlo, y esto es lo último que lo retiene. Al desecharlo
            // vuelve también su reserva del presupuesto.
            using var pdf = resultado.Valor;
            token.ThrowIfCancellationRequested();
            using var referencia = new DotNetStreamReference(pdf.Flujo, leaveOpen: true);
            _modulo ??= await JS.InvokeAsync<IJSObjectReference>("import", token, "./js/foto-a-pdf.js");
            await _modulo.InvokeVoidAsync("descargar", token, Textos["FotoNombreArchivo"].Value, referencia);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is PresupuestoMemoriaAgotadoException or CuotaSubidaMasivaSuperadaException or IOException)
        {
            Logger.LogInformation(ex, "Foto a PDF sin espacio para generar");
            _error = Textos["ErrorSinEspacio"];
        }
        catch (Exception ex) when (ex is JSException or JSDisconnectedException)
        {
            Logger.LogInformation("Foto a PDF: el navegador no pudo recibir la descarga ({Tipo}).", ex.GetType().Name);
            _error = Textos["FotoErrorDescarga"];
        }
        finally
        {
            _generando = false;
        }
    }

    private string MensajeDe(string codigo) => codigo switch
    {
        ErroresFotoAPdf.FotoDemasiadoGrande => Textos["FotoErrorDemasiadoGrande", LimitesFotoAPdf.TamanoMaximoFotoMb],
        ErroresFotoAPdf.TotalDemasiadoGrande => Textos["FotoErrorTotal", LimitesFotoAPdf.TamanoMaximoTotalMb],
        ErroresFotoAPdf.FormatoNoSoportado => Textos["FotoErrorFormato"],
        ErroresFotoAPdf.HeicNoSoportado => Textos["FotoErrorHeic"],
        ErroresFotoAPdf.DemasiadosPixeles => Textos["FotoErrorPixeles", LimitesFotoAPdf.MaximoPixeles / 1_000_000, LimitesFotoAPdf.MaximoLadoPixeles],
        ErroresFotoAPdf.DemasiadasFotos => Textos["FotoErrorDemasiadas", LimitesFotoAPdf.MaximoFotos],
        ErroresFotoAPdf.NoDisponible => Textos["NoDisponibleTitulo"],
        ErroresFotoAPdf.Ocupado => Textos["FotoErrorOcupado"],
        ErroresFotoAPdf.FotoVacia or ErroresFotoAPdf.FotoIlegible => Textos["FotoErrorIlegible"],
        _ => Textos["FotoErrorGenerar"],
    };

    private static string TextoTamano(long bytes) => bytes >= 1024 * 1024
        ? $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB"
        : $"{Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture)} KB";

    /// <summary>
    /// Construye la cadena data: de una miniatura. Virtual solo para que un test pueda hacerla fallar u observar lo reservado
    /// en ese momento (CaeManager.Web no tiene InternalsVisibleTo).
    /// </summary>
    protected virtual string CrearUrlMiniatura(byte[] jpeg) => PrefijoUrlMiniatura + Convert.ToBase64String(jpeg);

    /// <summary>
    /// Lo que se reserva por la miniatura de un JPEG de <paramref name="bytesJpeg"/> bytes, calculado ANTES de crear su cadena:
    /// la cadena data: (prefijo más 4 caracteres de Base64 por cada 3 bytes) a <see cref="BytesReservadosPorCaracter"/>.
    /// </summary>
    internal static long BytesReservadosDeMiniatura(int bytesJpeg) =>
        (PrefijoUrlMiniatura.Length + ((long)bytesJpeg + 2) / 3 * 4) * BytesReservadosPorCaracter;

    /// <summary>
    /// La reserva de una miniatura que se acaba de quitar no se devuelve todavía: su cadena sigue en el último lote de render
    /// enviado al navegador, que Blazor guarda serializado hasta que el navegador lo confirma. Se apunta con el número de
    /// renders empezados hasta ahora y se suelta en el <see cref="OnAfterRender"/> del primer render posterior, que en Blazor
    /// Server solo llega cuando el navegador confirmó ese lote (y con él todos los anteriores). Si ese render no llega, la
    /// suelta <see cref="DisposeAsync"/>.
    /// </summary>
    private void AplazarHastaElSiguientePintado(IDisposable? reserva)
    {
        if (reserva is not null)
            _reservasPorSoltar.Add((_pintadosEmpezados, reserva));
    }

    /// <summary>
    /// Cuenta los renders para <see cref="AplazarHastaElSiguientePintado"/>. ComponentBase llama a ShouldRender una vez por
    /// cada render que encola (el primero no pasa por aquí: <see cref="_pintadosEmpezados"/> empieza en 1). Un render que
    /// ya estaba encolado al quitar la foto cuenta como anterior: la reserva espera entonces a uno más, que es el lado seguro.
    /// </summary>
    protected override bool ShouldRender()
    {
        _pintadosEmpezados++;
        return true;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        // Los OnAfterRender llegan en el orden de los renders: este es el del render número _pintadosConfirmados.
        _pintadosConfirmados++;
        for (var i = _reservasPorSoltar.Count - 1; i >= 0; i--)
        {
            var (pintadoAlQuitar, reserva) = _reservasPorSoltar[i];
            if (pintadoAlQuitar < _pintadosConfirmados)
            {
                reserva.Dispose();
                _reservasPorSoltar.RemoveAt(i);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _desechado = true;
        _ciclo.Cancel();
        _almacen?.Dispose();
        foreach (var foto in _fotos)
            foto.ReservaMiniatura?.Dispose();
        foreach (var (_, reserva) in _reservasPorSoltar)
            reserva.Dispose();
        _reservasPorSoltar.Clear();
        _ciclo.Dispose();

        if (_modulo is not null)
        {
            try
            {
                await _modulo.DisposeAsync();
            }
            catch (JSDisconnectedException)
            {
            }
        }
    }
}

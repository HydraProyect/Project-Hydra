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
/// ya van giradas según el EXIF. La miniatura de cada foto vive en el circuito como bytes, acotada por
/// <see cref="LimitesFotoAPdf.TamanoMaximoMiniaturaBytes"/>, y reserva su tamaño real en el presupuesto de memoria hasta quitar
/// la foto o desechar la pantalla.
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
        ArchivoTemporal Archivo, string Nombre, long Bytes, FotoValidada Datos, byte[]? Miniatura, IDisposable? ReservaMiniatura);

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
                    var miniatura = await Task.Run(() => Procesador.CrearMiniatura(contenido, LadoMiniaturaPixeles, token), token);
                    if (miniatura.EsFallido)
                        codigoRechazo = miniatura.Error.Codigo;
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
            // al devolverla: se reserva aquí su tamaño real (sin esperar, para no esperar a una reserva ajena desde un circuito)
            // y se devuelve con la foto. Una miniatura que pasa del tope no se retiene: la foto entra sin vista previa.
            IDisposable? reservaMiniatura = null;
            if (bytesMiniatura is { Length: > 0 } && bytesMiniatura.Length <= LimitesFotoAPdf.TamanoMaximoMiniaturaBytes)
            {
                if (!PresupuestoMemoria.TryReservar(bytesMiniatura.Length, out var reserva))
                    throw new PresupuestoMemoriaAgotadoException();
                reservaMiniatura = reserva;
            }
            else
            {
                bytesMiniatura = null;
            }

            // La pantalla se desechó mientras se procesaba: nadie liberaría esta foto ni su reserva.
            if (_desechado)
            {
                reservaMiniatura?.Dispose();
                almacen.Eliminar(guardado);
                return;
            }

            _fotos.Add(new FotoElegida(guardado, nombre, guardado.TamanoBytes, datosFoto, bytesMiniatura, reservaMiniatura));
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
        foto.ReservaMiniatura?.Dispose();
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
            foto.ReservaMiniatura?.Dispose();
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
        ErroresFotoAPdf.FotoVacia or ErroresFotoAPdf.FotoIlegible => Textos["FotoErrorIlegible"],
        _ => Textos["FotoErrorGenerar"],
    };

    private static string TextoTamano(long bytes) => bytes >= 1024 * 1024
        ? $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB"
        : $"{Math.Max(1, bytes / 1024).ToString(CultureInfo.InvariantCulture)} KB";

    public async ValueTask DisposeAsync()
    {
        _desechado = true;
        _ciclo.Cancel();
        _almacen?.Dispose();
        foreach (var foto in _fotos)
            foto.ReservaMiniatura?.Dispose();
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

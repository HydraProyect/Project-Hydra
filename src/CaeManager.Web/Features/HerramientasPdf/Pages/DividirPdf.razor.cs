using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.HerramientasPdf.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.HerramientasPdf.Pages;

/// <summary>
/// Dividir un PDF con varios documentos: elegir el PDF, marcar los cortes y elegir la salida. «Guardar en TALVEG» sigue
/// con un paso por documento, revisar y crear; «Descargar» entrega cada tramo como un PDF aparte (con
/// <see cref="ExtraerTramoPdfQuery"/>) y no guarda nada. La detección es local y solo sugiere; cada alta pasa por
/// <see cref="CrearDocumentosDesdeDivisionCommand"/>, que delega en el alta normal. La Empresa gestionada sale del selector de la barra lateral, y sin selección no se puede subir nada;
/// solo si de ahí no se deduce una sola y hay varias Empresas propias se elige aquí.
///
/// El original vive en el almacén temporal del circuito, no en memoria, y desaparece al crear o al salir: no se conserva.
/// </summary>
public partial class DividirPdf : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Ancho con el que se rasteriza: el de «Grandes», que las otras dos escalan.</summary>
    private const int AnchoMiniaturaPixeles = 168;
    private const int AnchoVistaGrandePixeles = 720;

    /// <summary>Tope de vistas previas en un circuito; más allá, la página sale en blanco pero se puede cortar igual.</summary>
    private const int MaximoMiniaturas = 120;

    private const int MiniaturasPorRepintado = 4;

    private const string ClaveDividir = "dividir";
    private const string ClaveAsignar = "asignar";
    private const string ClaveRevisar = "revisar";

    private enum PasoAsistente { Subir, Dividir, Asignar, Revisar, Resultado }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private CuotaSubidaMasiva CuotaSubidaMasiva { get; set; } = default!;
    [Inject] private IPresupuestoMemoriaDocumentos PresupuestoMemoria { get; set; } = default!;
    [Inject] private CaeManager.Application.DocumentosIa.Common.IRasterizadorPaginasPdfService Rasterizador { get; set; } = default!;
    [Inject] private IDisponibilidadHerramientasPdf Disponibilidad { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Reloj { get; set; } = default!;
    [Inject] private ILogger<DividirPdf> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosHerramientasPdf> Textos { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private readonly Dictionary<int, string> _miniaturas = [];
    private readonly Dictionary<int, string> _grandes = [];
    private readonly HashSet<int> _grandesPedidas = [];

    private bool _inicializando = true;
    private bool _disponible;
    private bool _soloLectura;
    private bool _sinEmpresa;
    private bool _elegirEmpresaAqui;
    private bool _selectorVisible;
    private bool _cargando;
    private bool _creando;
    private bool _descargando;
    private int? _descargados;
    private IJSObjectReference? _modulo;
    private bool _firmado;
    private bool _textoLegible = true;
    private bool _desechado;
    private string? _error;
    private string _nombreOriginal = string.Empty;
    private string _horaCreacion = string.Empty;
    private int _tamanoPaginas = 112;
    private int _indiceTramo;
    private int? _paginaAmpliada;
    private Guid? _empresaId;
    private Guid? _tenantEsperadoId;
    private IReadOnlyList<EmpresaSelectorDto> _empresas = [];
    private PasoAsistente _paso = PasoAsistente.Subir;
    private EstadoDivisionPdf? _division;
    private AlmacenTemporalSubidaMasiva? _almacen;
    private ArchivoTemporal? _original;
    private CancellationTokenSource? _vistas;
    private List<(AsignacionTramo Asignacion, ResultadoTramoCreado Resultado)> _resultados = [];

    private bool PuedeSubir => !_sinEmpresa && !_soloLectura && _empresaId is not null && _tenantEsperadoId is not null;

    private static long LimiteDocumentoMb => LimitesArchivoSubido.TamanoMaximoBytes / (1024 * 1024);

    private string ClavePasoActual => _paso switch
    {
        PasoAsistente.Asignar => ClaveAsignar,
        PasoAsistente.Revisar => ClaveRevisar,
        _ => ClaveDividir,
    };

    private IReadOnlySet<string> PasosCompletados => _paso switch
    {
        PasoAsistente.Asignar => new HashSet<string> { ClaveDividir },
        PasoAsistente.Revisar => new HashSet<string> { ClaveDividir, ClaveAsignar },
        _ => new HashSet<string>(),
    };

    private IReadOnlyList<PasoDefinicion> Pasos(EstadoDivisionPdf division) =>
    [
        new(ClaveDividir, Textos["PasoDividir"]),
        new(ClaveAsignar, Textos[division.Rangos.Count == 1 ? "PasoAsignarUno" : "PasoAsignarVarios", division.Rangos.Count]),
        new(ClaveRevisar, Textos["PasoRevisar"]),
    ];

    protected override async Task OnInitializedAsync()
    {
        _disponible = await Disponibilidad.EstaDisponibleAsync(_ciclo.Token);
        if (!_disponible)
        {
            _inicializando = false;
            return;
        }

        // Por la puerta: la página se inicializa a la vez que el layout, que comparte el DbContext.
        _soloLectura = await PuertaAccesoDatos.EjecutarAsync(() => CurrentUserService.ObtenerRolEfectivoAsync()) == Roles.Consulta;
        var tenantOrigenId = await PuertaAccesoDatos.EjecutarAsync(() => CurrentUserService.ObtenerTenantOrigenIdAsync());
        if (!_desechado)
            _almacen = CuotaSubidaMasiva.CrearAlmacen(tenantOrigenId);

        var contexto = await PuertaAccesoDatos.EjecutarAsync(
            () => ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, _ciclo.Token));
        _sinEmpresa = contexto.SinSeleccion;
        _selectorVisible = contexto.Activa is not null;
        _tenantEsperadoId = TenantActual.TenantId;

        // La Empresa gestionada sale del selector de la barra lateral o de la unica Empresa del Tenant. Ese selector
        // elige un Tenant, no una fila Empresa: el cruce es por nombre, y si no da una sola Empresa se elige aqui.
        if (!_sinEmpresa)
        {
            _empresas = await PuertaAccesoDatos.EjecutarAsync(
                () => Mediator.Send(new ObtenerEmpresasParaSelectorQuery(), _ciclo.Token));
            var coincidentes = contexto.Activa is { } activa
                ? _empresas.Where(e => string.Equals(e.RazonSocial, activa.Nombre, StringComparison.OrdinalIgnoreCase)).ToList()
                : [];
            if (coincidentes.Count == 1)
                _empresaId = coincidentes[0].Id;
            else if (_empresas.Count == 1)
                _empresaId = _empresas[0].Id;
            else if (_empresas.Count > 1)
                _elegirEmpresaAqui = true;
        }

        _inicializando = false;
    }

    private string? NombreEmpresaActiva => _empresas.FirstOrDefault(e => e.Id == _empresaId)?.RazonSocial;

    protected override async Task OnAfterRenderAsync(bool primeraVez)
    {
        await base.OnAfterRenderAsync(primeraVez);
        if (_division is not { } division || _desechado)
            return;

        if (_paso == PasoAsistente.Asignar && division.Rangos.Count > 0)
            _ = CargarVistaGrandeAsync(division.Rangos[Math.Clamp(_indiceTramo, 0, division.Rangos.Count - 1)].Inicial);
        if (_paginaAmpliada is { } ampliada)
            _ = CargarVistaGrandeAsync(ampliada);
    }

    /// <summary>Vista de una sola pagina a tamano de lectura, bajo demanda: solo la del tramo abierto o ampliada.</summary>
    private async Task CargarVistaGrandeAsync(int pagina)
    {
        if (_almacen is not { } almacen || _original is not { } original || !_grandesPedidas.Add(pagina))
            return;

        var token = _ciclo.Token;
        try
        {
            using var reserva = await PresupuestoMemoria.ReservarAsync(original.TamanoBytes, token);
            var contenido = await almacen.LeerAsync(original, token);
            var vista = await Task.Run(() => Rasterizador.RasterizarMiniatura(contenido, pagina - 1, AnchoVistaGrandePixeles, token), token);
            if (vista.EsFallido)
                return;

            _grandes[pagina] = Convert.ToBase64String(vista.Valor);
            await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (ex is PresupuestoMemoriaAgotadoException or IOException)
        {
            _grandesPedidas.Remove(pagina);
            Logger.LogInformation(ex, "Dividir PDF sigue sin vista grande");
        }
    }

    private string? Grande(int pagina) => _grandes.GetValueOrDefault(pagina);

    private void ElegirTamano(string valor) =>
        _tamanoPaginas = int.TryParse(valor, CultureInfo.InvariantCulture, out var tamano) ? tamano : _tamanoPaginas;

    private async Task CargarArchivoAsync(InputFileChangeEventArgs evento)
    {
        if (_cargando || _almacen is null || _empresaId is not { } empresaId || _tenantEsperadoId is null)
            return;

        var archivo = evento.File;
        _error = null;

        if (archivo.Size > LimitesDivisionPdf.TamanoMaximoOriginalBytes)
        {
            _error = Textos["ErrorDemasiadoGrande", LimitesDivisionPdf.TamanoMaximoOriginalMb];
            return;
        }

        _cargando = true;
        await InvokeAsync(StateHasChanged);
        ArchivoTemporal? guardado = null;
        try
        {
            guardado = await _almacen.GuardarAsync(
                () => archivo.OpenReadStream(LimitesDivisionPdf.TamanoMaximoOriginalBytes, _ciclo.Token),
                archivo.Size, LimitesDivisionPdf.TamanoMaximoOriginalBytes, cancellationToken: _ciclo.Token);

            Result<PreparacionDivisionPdf> preparacion;
            ReservaMemoriaDocumentos? reserva = await PresupuestoMemoria.ReservarAsync(guardado.TamanoBytes, _ciclo.Token);
            try
            {
                var contenido = await _almacen.LeerAsync(guardado, _ciclo.Token);
                preparacion = await Mediator.Send(new PrepararDivisionPdfQuery(contenido, empresaId), _ciclo.Token);
            }
            catch (EntradaPdfRetenidaException)
            {
                // El proceso aparte que recibió el PDF no consta terminado y puede seguir referenciándolo: no se devuelve.
                EntradaPdfRetenidaException.Retener(reserva);
                reserva = null;
                throw;
            }
            finally
            {
                reserva?.Dispose();
            }

            if (preparacion.EsFallido)
            {
                _almacen.Eliminar(guardado);
                _error = MensajeDeLectura(preparacion.Error);
                return;
            }

            var preparado = preparacion.Valor;
            _original = guardado;
            _nombreOriginal = archivo.Name;
            _firmado = preparado.EstaFirmado;
            _textoLegible = preparado.TextoLegible;
            _division = new EstadoDivisionPdf(preparado.NumeroPaginas, preparado.Tramos, preparado.Catalogo);
            _paso = PasoAsistente.Dividir;
            _ = GenerarVistasAsync(guardado);
        }
        catch (ArchivoTemporalDemasiadoGrandeException)
        {
            _error = Textos["ErrorDemasiadoGrande", LimitesDivisionPdf.TamanoMaximoOriginalMb];
        }
        catch (Exception ex) when (ex is CuotaSubidaMasivaSuperadaException or PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException)
        {
            if (guardado is not null)
                _almacen.Eliminar(guardado);
            _error = Textos["ErrorSinEspacio"];
        }
        finally
        {
            _cargando = false;
        }
    }

    private string MensajeDeLectura(Error error) => error.Codigo switch
    {
        ErroresDivisorPdf.ConContrasena => Textos["ErrorContrasena"],
        ErroresDivisorPdf.DemasiadoGrande => Textos["ErrorDemasiadoGrande", LimitesDivisionPdf.TamanoMaximoOriginalMb],
        ErroresDivisorPdf.DemasiadoComplejo => Textos["ErrorDemasiadoComplejo"],
        ErroresDivisorPdf.Ilegible or ErroresDivisorPdf.DemasiadasPaginas => Textos["ErrorIlegible"],
        _ => error.Mensaje,
    };

    /// <summary>
    /// Mejor esfuerzo y local: una página sin vista previa se puede cortar igual. Se reparte el trabajo para que el
    /// circuito siga respondiendo mientras se generan.
    /// </summary>
    private async Task GenerarVistasAsync(ArchivoTemporal original)
    {
        var division = _division;
        if (_almacen is not { } almacen || division is null)
            return;

        _vistas?.Cancel();
        _vistas = CancellationTokenSource.CreateLinkedTokenSource(_ciclo.Token);
        var token = _vistas.Token;
        try
        {
            using var reserva = await PresupuestoMemoria.ReservarAsync(original.TamanoBytes, token);
            var contenido = await almacen.LeerAsync(original, token);
            var total = Math.Min(division.NumeroPaginas, MaximoMiniaturas);
            var pendientesDeRepintar = 0;

            for (var indice = 0; indice < total; indice++)
            {
                var pagina = indice;
                var vista = await Task.Run(() => Rasterizador.RasterizarMiniatura(contenido, pagina, AnchoMiniaturaPixeles, token), token);
                if (vista.EsFallido)
                    continue;

                _miniaturas[pagina + 1] = Convert.ToBase64String(vista.Valor);
                if (++pendientesDeRepintar == MiniaturasPorRepintado)
                {
                    pendientesDeRepintar = 0;
                    await InvokeAsync(StateHasChanged);
                }
            }

            if (pendientesDeRepintar > 0)
                await InvokeAsync(StateHasChanged);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Se cambió de PDF o se retiró la página.
        }
        catch (Exception ex) when (ex is PresupuestoMemoriaAgotadoException or IOException)
        {
            Logger.LogInformation(ex, "Dividir PDF sigue sin vistas previas");
        }
    }

    private string? Miniatura(int pagina) => _miniaturas.GetValueOrDefault(pagina);

    private void MoverPaginaAmpliada(int salto)
    {
        if (_paginaAmpliada is { } pagina && _division is { } division)
            _paginaAmpliada = Math.Clamp(pagina + salto, 1, division.NumeroPaginas);
    }

    private static Action<ChangeEventArgs> AlMarcarOmitido(AsignacionTramo asignacion) =>
        e => asignacion.Omitido = e.Value is true;

    private static Action<ChangeEventArgs> AlMarcarSinCaducidad(AsignacionTramo asignacion) =>
        e => asignacion.SinCaducidad = e.Value is true;

    // Durante una descarga no se tocan los cortes: la serie entrega los rangos con los que empezó.
    private void AlternarCorte(int pagina)
    {
        if (_descargando)
            return;
        _division?.AlternarCorte(pagina);
        _descargados = null;
    }

    private void ConfirmarPropuestas()
    {
        if (_descargando)
            return;
        _division?.ConfirmarPropuestas();
        _descargados = null;
    }

    private void IrAAsignar()
    {
        if (_descargando)
            return;
        _indiceTramo = 0;
        _error = null;
        _paso = PasoAsistente.Asignar;
    }

    private Task IrAlPasoAsync(string clave)
    {
        _error = null;
        _paso = clave switch
        {
            ClaveDividir => PasoAsistente.Dividir,
            ClaveAsignar => PasoAsistente.Asignar,
            _ => _paso,
        };
        return Task.CompletedTask;
    }

    private void AnteriorTramo()
    {
        if (_indiceTramo == 0)
            _paso = PasoAsistente.Dividir;
        else
            _indiceTramo--;
    }

    private void SiguienteTramo()
    {
        if (_division is null)
            return;

        if (_indiceTramo >= _division.Rangos.Count - 1)
            _paso = PasoAsistente.Revisar;
        else
            _indiceTramo++;
    }

    private void Corregir(int indice)
    {
        _indiceTramo = indice;
        _paso = PasoAsistente.Asignar;
    }

    private async Task CrearAsync()
    {
        if (_creando || _division is not { } division || _almacen is null || _original is null
            || _empresaId is not { } empresaId || _tenantEsperadoId is not { } tenantId)
            return;

        var tramos = division.TramosACrear();
        if (tramos.Count == 0)
            return;

        _creando = true;
        _error = null;
        try
        {
            Result<IReadOnlyList<ResultadoTramoCreado>> creacion;
            // La copia del original y lo que se recibe del proceso aparte por cada tramo, de uno en uno.
            ReservaMemoriaDocumentos? reserva = await PresupuestoMemoria.ReservarAsync(
                LimitesDivisionPdf.MemoriaCreacionTramos(_original.TamanoBytes), _ciclo.Token);
            try
            {
                var contenido = await _almacen.LeerAsync(_original, _ciclo.Token);
                creacion = await Mediator.Send(
                    new CrearDocumentosDesdeDivisionCommand(contenido, _nombreOriginal, tenantId, empresaId, tramos), _ciclo.Token);
                if (creacion.EsExitoso && creacion.Valor.Any(r => r.EntradaRetenida))
                {
                    // Lote cortado con Documentos ya creados por un proceso aparte que no consta terminado: el resultado
                    // se muestra, pero la reserva del original tampoco se devuelve.
                    EntradaPdfRetenidaException.Retener(reserva);
                    reserva = null;
                }
            }
            catch (EntradaPdfRetenidaException)
            {
                // Sin ningún Documento creado: el proceso aparte no consta terminado y la reserva no se devuelve.
                EntradaPdfRetenidaException.Retener(reserva);
                reserva = null;
                throw;
            }
            finally
            {
                reserva?.Dispose();
            }

            if (creacion.EsFallido)
            {
                _error = creacion.Error.Codigo == CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado
                    ? Textos["ErrorTenantCambiado"]
                    : creacion.Error.Mensaje;
                return;
            }

            _resultados = creacion.Valor
                .Select(r => (division.Asignacion(new RangoPaginas(r.PaginaInicial, r.PaginaFinal)), r))
                .ToList();
            _horaCreacion = DiaDeNegocio.AhoraEnHoraPeninsular().ToString("HH:mm", CultureInfo.InvariantCulture);
            _paso = PasoAsistente.Resultado;
            RetirarOriginal();
        }
        catch (Exception ex) when (ex is PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException)
        {
            _error = Textos["ErrorSinEspacio"];
        }
        finally
        {
            _creando = false;
        }
    }

    /// <summary>
    /// Lo que se espera a que el navegador lea cada tramo. Como en Unir PDF: pasar un token propio anula el plazo por
    /// defecto de JS, y sin él un navegador que no tira del flujo retendría la reserva mientras viva el circuito.
    /// </summary>
    private static readonly TimeSpan PlazoDescarga = TimeSpan.FromMinutes(1);

    /// <summary>
    /// La salida «Descargar»: cada tramo, en orden, como un PDF aparte, sin crear ningún Documento. Por tramo hay una sola
    /// reserva: mientras se lee y se extrae cubre todo lo que coexiste (<see cref="LimitesDivisionPdf.MemoriaExtraccionTramo"/>),
    /// y en cuanto el tramo existe se reduce a él, sin soltarse nunca mientras el tramo sigue vivo, así que la espera del
    /// navegador no retiene el original pero el tramo nunca queda fuera del presupuesto. Un tramo que falla, por el
    /// motivo que sea, corta la serie y se dice cuál, para no dejar a medias una descarga que parezca completa.
    /// </summary>
    private async Task DescargarTramosAsync()
    {
        if (_descargando || _creando || _division is not { } division || _almacen is not { } almacen
            || _original is not { } original || _empresaId is not { } empresaId || _tenantEsperadoId is not { } tenantId)
            return;

        var rangos = division.Rangos;
        var nombre = _nombreOriginal;
        var token = _ciclo.Token;
        var entregados = 0;
        RangoPaginas? enCurso = null;
        var tenantCambiado = false;
        var tramoDemasiadoGrande = false;
        var tramoDemasiadoComplejo = false;
        var falloInterno = false;
        var empresaFuera = false;
        _descargando = true;
        _descargados = null;
        _error = null;
        try
        {
            foreach (var rango in rangos)
            {
                enCurso = rango;
                // Antes de extraer: así, entre la última comprobación de acceso de la extracción y la entrega del tramo no
                // queda ninguna espera (ni la del módulo, que solo se importa la primera vez).
                if (!await ImportarModuloDescargaAsync(token))
                    break;

                var entregado = await ExtraerYEntregarAsync(almacen, original, nombre, tenantId, empresaId, rango, token);
                if (entregado.Fallo is { } codigo)
                {
                    tenantCambiado = codigo == CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado;
                    tramoDemasiadoGrande = codigo == ErroresDivisorPdf.DemasiadoGrande;
                    tramoDemasiadoComplejo = codigo == ErroresDivisorPdf.DemasiadoComplejo;
                    falloInterno = codigo == ErroresDivisorPdf.FalloInterno;
                    empresaFuera = codigo == ObtenerCatalogoDivisionPdfQueryHandler.ErrorEmpresaNoGestionable;
                    break;
                }

                if (!entregado.Recibido)
                    break;

                entregados++;
                enCurso = null;
            }

            // Con otro Tenant en el selector o la Empresa fuera de alcance, reintentar no sirve, y lo ya descargado no se
            // puede deshacer: se dice cuánto salió. Un tramo demasiado grande tampoco cabe al reintentarlo igual.
            var descargado = entregados == 0 ? Textos["DescargaNinguna"].Value : Textos["DescargaParcial", entregados, rangos.Count].Value;
            if (tenantCambiado)
                _error = Textos["ErrorTenantCambiadoDescarga", descargado];
            else if (empresaFuera)
                _error = Textos["ErrorEmpresaFueraDescarga", descargado];
            else if (enCurso is { } fallido)
                _error = tramoDemasiadoGrande
                    ? Textos["ErrorTramoDemasiadoGrande", fallido.Inicial, fallido.Final, LimitesArchivoSubido.TamanoMaximoMb]
                    // El proceso aparte se quedó sin memoria, sin tiempo o murió: reintentar ocuparía otra vez el único turno.
                    : tramoDemasiadoComplejo
                        ? Textos["ErrorTramoDemasiadoComplejo", fallido.Inicial, fallido.Final]
                        // No se pudo usar el proceso aparte: el PDF no se llegó a juzgar, y no se dice que esté dañado.
                        : falloInterno
                            ? Textos["ErrorTramoFalloInterno", fallido.Inicial, fallido.Final]
                            : Textos["ErrorDescargaTramo", fallido.Inicial, fallido.Final];
        }
        catch (Exception ex) when (ex is PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException)
        {
            // Solo lanzan la reserva, su ampliación y el proceso aparte (sin turno, o sin constar terminado), dentro del
            // bucle y con el tramo ya en curso; el aviso genérico es solo por si eso cambiara.
            _error = enCurso is { } sinEspacio
                ? Textos["ErrorSinEspacioTramo", sinEspacio.Inicial, sinEspacio.Final]
                : Textos["ErrorSinEspacio"];
        }
        catch (IOException ex)
        {
            // El temporal del original ha desaparecido o el disco ha fallado: se dice qué tramo no salió.
            Logger.LogWarning(ex, "Dividir PDF no pudo leer el original para descargar un tramo");
            if (enCurso is { } fallido)
                _error = Textos["ErrorDescargaTramo", fallido.Inicial, fallido.Final];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // La pantalla se ha desechado: no queda nadie a quien avisar.
        }
        finally
        {
            _descargando = false;
            // Si a mitad se cambió de PDF, el recuento ya no describe lo que hay en pantalla.
            _descargados = entregados > 0 && ReferenceEquals(_division, division) ? entregados : null;
        }
    }

    /// <summary>
    /// Un tramo: lo extrae con su reserva, y si sale, lo entrega. <c>Fallo</c> es el código por el que no se entregó sin
    /// llegar al navegador; <c>Recibido</c>, si el navegador lo leyó.
    /// </summary>
    private async Task<(string? Fallo, bool Recibido)> ExtraerYEntregarAsync(AlmacenTemporalSubidaMasiva almacen,
        ArchivoTemporal original, string nombre, Guid tenantId, Guid empresaId, RangoPaginas rango, CancellationToken token)
    {
        ReservaMemoriaDocumentos? reserva = await PresupuestoMemoria.ReservarAsync(LimitesDivisionPdf.MemoriaExtraccionTramo(original.TamanoBytes), token);
        try
        {
            Result<TramoPdfExtraido> tramo;
            try
            {
                tramo = await ExtraerTramoAsync(almacen, original, nombre, tenantId, empresaId, rango, token);
            }
            catch (EntradaPdfRetenidaException)
            {
                // El proceso aparte que recibió el original no consta terminado y puede seguir referenciándolo.
                EntradaPdfRetenidaException.Retener(reserva);
                reserva = null;
                throw;
            }

            if (tramo.EsFallido)
                return (tramo.Error.Codigo, false);

            // Lo último antes de entregar, sin ninguna espera detrás hasta el navegador: si el Tenant activo ya no es el
            // del inicio, el tramo no sale de TALVEG. La Empresa la acaba de comprobar la extracción, en su última espera.
            if (TenantActual.TenantId != tenantId)
                return (CrearDocumentosDesdeDivisionCommandHandler.ErrorTenantCruzado, false);

            using var exceso = CubrirSoloElTramo(reserva, tramo.Valor.Contenido.Length);
            return (null, await EntregarTramoAsync(tramo.Valor, token));
        }
        finally
        {
            reserva?.Dispose();
        }
    }

    /// <summary>Importa el módulo de descarga la primera vez; <c>false</c> si el navegador no lo carga en el plazo.</summary>
    private async Task<bool> ImportarModuloDescargaAsync(CancellationToken token)
    {
        if (_modulo is not null)
            return true;

        using var vencimiento = new CancellationTokenSource(PlazoDescarga, Reloj);
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(token, vencimiento.Token);
        try
        {
            _modulo = await JS.InvokeAsync<IJSObjectReference>("import", plazo.Token, "./js/descarga-pdf-unido.js");
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && vencimiento.IsCancellationRequested)
        {
            Logger.LogWarning("Dividir PDF: el navegador no cargó el módulo de descarga en {Plazo}.", PlazoDescarga);
            return false;
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Dividir PDF no pudo cargar el módulo de descarga");
            return false;
        }
    }

    /// <summary>Lee el original y extrae un tramo; la copia del original no sobrevive a este método.</summary>
    private async Task<Result<TramoPdfExtraido>> ExtraerTramoAsync(AlmacenTemporalSubidaMasiva almacen, ArchivoTemporal original,
        string nombre, Guid tenantId, Guid empresaId, RangoPaginas rango, CancellationToken token)
    {
        var contenido = await almacen.LeerAsync(original, token);
        return await Mediator.Send(
            new ExtraerTramoPdfQuery(contenido, nombre, tenantId, empresaId, rango.Inicial, rango.Final), token);
    }

    /// <summary>
    /// Deja la reserva de la extracción en lo que ocupa el tramo, que sigue vivo hasta que el navegador lo recibe. El tramo
    /// no puede pasar de <see cref="LimitesDivisionPdf.TamanoMaximoTramo"/>, que la reserva cubre dos veces; si aun así
    /// ocupara más, amplía sin esperar (esperar reteniendo una reserva puede esperar a sí misma) o falla cerrado.
    /// </summary>
    /// <returns>La ampliación, si hizo falta; quien llama la suelta junto con la reserva.</returns>
    /// <exception cref="PresupuestoMemoriaAgotadoException">El tramo no cabe ahora junto a lo ya reservado.</exception>
    private ReservaMemoriaDocumentos? CubrirSoloElTramo(ReservaMemoriaDocumentos reserva, long bytesTramo)
    {
        if (bytesTramo <= reserva.Bytes)
        {
            reserva.ReducirA(bytesTramo);
            return null;
        }

        return PresupuestoMemoria.TryReservar(bytesTramo - reserva.Bytes, out var exceso)
            ? exceso
            : throw new PresupuestoMemoriaAgotadoException();
    }

    /// <summary>
    /// Entrega un tramo al navegador como flujo; vuelve cuando lo ha leído, ha fallado o vence el plazo. El módulo ya está
    /// importado (<see cref="ImportarModuloDescargaAsync"/>): la primera espera de aquí es ya la entrega.
    /// </summary>
    private async Task<bool> EntregarTramoAsync(TramoPdfExtraido tramo, CancellationToken token)
    {
        using var vencimiento = new CancellationTokenSource(PlazoDescarga, Reloj);
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(token, vencimiento.Token);
        try
        {
            using var referencia = new DotNetStreamReference(new MemoryStream(tramo.Contenido, writable: false));
            await _modulo!.InvokeVoidAsync("descargarPdf", plazo.Token, tramo.NombreArchivo, referencia);
            return true;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && vencimiento.IsCancellationRequested)
        {
            Logger.LogWarning("Dividir PDF: el navegador no leyó un tramo en {Plazo}; se suelta.", PlazoDescarga);
            return false;
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Dividir PDF no pudo iniciar la descarga de un tramo");
            return false;
        }
    }

    private void RetirarOriginal()
    {
        _vistas?.Cancel();
        if (_original is not null)
            _almacen?.Eliminar(_original);
        _original = null;
    }

    private Task ReiniciarAsync()
    {
        if (_descargando)
            return Task.CompletedTask;
        RetirarOriginal();
        _division = null;
        _miniaturas.Clear();
        _grandes.Clear();
        _grandesPedidas.Clear();
        _resultados = [];
        _error = null;
        _descargados = null;
        _firmado = false;
        _textoLegible = true;
        _indiceTramo = 0;
        _paso = PasoAsistente.Subir;
        return Task.CompletedTask;
    }

    private string TextoPaginas(RangoPaginas rango) =>
        rango.Paginas == 1 ? Textos["Pagina", rango.Inicial] : Textos["Paginas", rango.Inicial, rango.Final];

    /// <summary>Motivo legible, sin porcentajes: el usuario tiene que poder comprobarlo en la página citada.</summary>
    private string TextoMotivo(TramoDetectado tramo) => tramo.PaginaMotivo is { } pagina
        ? Textos[$"Motivo{tramo.Motivo}", pagina]
        : Textos[$"Motivo{tramo.Motivo}"];

    private string TextoCaducidad(EstadoDivisionPdf division, AsignacionTramo asignacion)
    {
        if (asignacion.SinCaducidad)
            return Textos["SinCaducidad"];
        if (division.Tipo(asignacion.TipoDocumentoId) is { VencimientoAutomatico: true })
            return Textos["CaducidadAutomaticaCorta"];
        return asignacion.FechaCaducidad?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? Textos["SinFecha"];
    }

    private string TextoPendientes(IReadOnlyList<DatoPendiente> pendientes) =>
        Textos["FaltaParaCrear", string.Join(", ", pendientes.Select(p => Textos[$"Falta{p}"].Value))];

    private static string Texto(Guid? id) => id?.ToString() ?? string.Empty;

    private static Guid? Identificador(string valor) => Guid.TryParse(valor, out var id) ? id : null;

    private static string TextoFecha(DateOnly? fecha) =>
        fecha?.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture) ?? string.Empty;

    private static DateOnly? Fecha(string valor) =>
        DateOnly.TryParseExact(valor?.Trim(), "dd/MM/yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha) ? fecha : null;

    public void Dispose()
    {
        _desechado = true;
        _ciclo.Cancel();
        _vistas?.Dispose();
        _almacen?.Dispose();
        _ciclo.Dispose();
        if (_modulo is not null)
            _ = _modulo.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
    }
}

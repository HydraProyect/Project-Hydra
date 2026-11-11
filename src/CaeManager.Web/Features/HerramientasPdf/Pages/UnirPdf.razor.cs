using System.Globalization;
using CaeManager.Application.Common;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.HerramientasPdf;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Documentos;
using CaeManager.Web.Features.HerramientasPdf.Recursos;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;

namespace CaeManager.Web.Features.HerramientasPdf.Pages;

/// <summary>
/// Unir varios PDF en uno: los sube la persona o los toma de Documentos de la Empresa gestionada, los ordena y descarga el
/// resultado. Nada se guarda como Documento. La lectura de Documentos, el alcance de cartera y el registro de cada acceso
/// sensible viven en <see cref="UnirPdfQuery"/>; aquí solo se arma la lista y se traducen los fallos.
///
/// La Empresa gestionada sale del selector de la barra lateral, como en Dividir. Los PDF subidos viven en el almacén
/// temporal del circuito, no en memoria, y desaparecen al unir o al salir. Los Documentos no llegan a la pantalla: se pasa
/// solo su Id y el contenido se lee en Application.
/// </summary>
public partial class UnirPdf : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    private sealed class ElementoUnion(string nombre, Guid? documentoId, ArchivoTemporal? archivo, int? paginas, bool firmado)
    {
        public string Nombre { get; } = nombre;
        public Guid? DocumentoId { get; } = documentoId;
        public ArchivoTemporal? Archivo { get; } = archivo;
        public int? Paginas { get; } = paginas;
        public bool Firmado { get; } = firmado;
    }

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;
    [Inject] private ICurrentUserService CurrentUserService { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private CuotaSubidaMasiva CuotaSubidaMasiva { get; set; } = default!;
    [Inject] private IPresupuestoMemoriaDocumentos PresupuestoMemoria { get; set; } = default!;
    [Inject] private IDisponibilidadHerramientasPdf Disponibilidad { get; set; } = default!;
    [Inject] private IJSRuntime JS { get; set; } = default!;
    [Inject] private TimeProvider Reloj { get; set; } = default!;
    [Inject] private ILogger<UnirPdf> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosHerramientasPdf> Textos { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private readonly List<ElementoUnion> _elementos = [];

    private bool _inicializando = true;
    private bool _disponible;
    private bool _sinEmpresa;
    private bool _elegirEmpresaAqui;
    private bool _selectorVisible;
    private bool _cargando;
    private bool _uniendo;
    private bool _unido;
    private bool _desechado;
    private bool _documentosAbiertos;
    private bool _cargandoDocumentos;
    private string? _error;
    private string _nombreUnido = string.Empty;
    private int _paginasUnido;
    private Guid? _empresaId;
    private Guid? _tenantEsperadoId;
    private IReadOnlyList<EmpresaSelectorDto> _empresas = [];
    private IReadOnlyList<DocumentoParaUnirDto> _documentos = [];
    private IReadOnlyList<int> _firmasPendientes = [];
    private string? _huellaFirmas;
    private bool _hayMasDocumentos;
    private AlmacenTemporalSubidaMasiva? _almacen;
    private IJSObjectReference? _modulo;

    private static long LimiteSubidaMb => LimitesArchivoSubido.TamanoMaximoBytes / (1024 * 1024);

    private bool PuedeAnadir => !_sinEmpresa && _empresaId is not null && _tenantEsperadoId is not null;

    private bool EstaAnadido(Guid documentoId) => _elementos.Any(e => e.DocumentoId == documentoId);

    private List<(ElementoUnion Elemento, int Posicion)> ElementosConPosicion => _elementos.Select((e, i) => (e, i)).ToList();

    private bool PuedeUnir => PuedeAnadir && !_uniendo && !_cargando && _elementos.Count >= LimitesUnionPdf.MinimoFicheros;

    private string? NombreEmpresaActiva => _empresas.FirstOrDefault(e => e.Id == _empresaId)?.RazonSocial;

    protected override async Task OnInitializedAsync()
    {
        _disponible = await Disponibilidad.EstaDisponibleAsync(_ciclo.Token);
        if (!_disponible)
        {
            _inicializando = false;
            return;
        }

        // Por la puerta: la página se inicializa a la vez que el layout, que comparte el DbContext.
        var tenantOrigenId = await PuertaAccesoDatos.EjecutarAsync(() => CurrentUserService.ObtenerTenantOrigenIdAsync());
        if (!_desechado)
            _almacen = CuotaSubidaMasiva.CrearAlmacen(tenantOrigenId);

        var contexto = await PuertaAccesoDatos.EjecutarAsync(
            () => ContextoEmpresaActiva.ResolverAsync(Mediator, TenantActual, _ciclo.Token));
        _sinEmpresa = contexto.SinSeleccion;
        _selectorVisible = contexto.Activa is not null;
        _tenantEsperadoId = TenantActual.TenantId;

        // La Empresa gestionada sale del selector de la barra lateral o de la única Empresa del Tenant. Ese selector elige
        // un Tenant, no una fila Empresa: el cruce es por nombre, y si no da una sola Empresa se elige aquí.
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

    private async Task CambiarEmpresaAsync(string valor)
    {
        var nueva = Identificador(valor);
        if (nueva == _empresaId || _uniendo)
            return;

        // Los Documentos de la Empresa anterior no se unen en la nueva.
        _empresaId = nueva;
        _documentos = [];
        _hayMasDocumentos = false;
        OlvidarFirmas();
        _error = null;
        _elementos.RemoveAll(e => e.DocumentoId is not null);
        if (_documentosAbiertos)
            await CargarDocumentosAsync();
    }

    private async Task AlternarDocumentosAsync()
    {
        _documentosAbiertos = !_documentosAbiertos;
        if (_documentosAbiertos)
            await CargarDocumentosAsync();
    }

    private async Task CargarDocumentosAsync()
    {
        if (_empresaId is not { } empresaId)
            return;

        _cargandoDocumentos = true;
        _error = null;
        try
        {
            var lista = await PuertaAccesoDatos.EjecutarAsync(
                () => Mediator.Send(new ListarDocumentosParaUnirQuery(empresaId), _ciclo.Token));
            if (lista.EsFallido)
            {
                _documentos = [];
                _hayMasDocumentos = false;
                _error = MensajeDeOperacion(lista.Error);
                return;
            }

            _documentos = lista.Valor.Documentos;
            _hayMasDocumentos = lista.Valor.HayMas;
        }
        finally
        {
            _cargandoDocumentos = false;
        }
    }

    private void AnadirDocumento(DocumentoParaUnirDto documento)
    {
        // Mientras se une, la lista no se toca: UnirAsync la recorre con esperas y quitar un elemento rompería su lectura.
        if (_uniendo || _elementos.Count >= LimitesUnionPdf.MaximoFicheros || _elementos.Any(e => e.DocumentoId == documento.Id))
            return;

        OlvidarFirmas();
        _elementos.Add(new ElementoUnion(
            documento.TitularNombre is { } titular ? $"{documento.TipoNombre} · {titular}" : documento.TipoNombre,
            documento.Id, null, null, false));
    }

    private async Task CargarArchivosAsync(InputFileChangeEventArgs evento)
    {
        if (_cargando || _uniendo || _almacen is null || !PuedeAnadir)
            return;

        IReadOnlyList<IBrowserFile> archivos;
        try
        {
            archivos = evento.GetMultipleFiles(LimitesUnionPdf.MaximoFicheros);
        }
        catch (InvalidOperationException)
        {
            _error = Textos["UnirErrorMaximoFicheros", LimitesUnionPdf.MaximoFicheros];
            return;
        }

        _error = null;
        OlvidarFirmas();
        _cargando = true;
        await InvokeAsync(StateHasChanged);

        var errores = new List<string>();
        try
        {
            foreach (var archivo in archivos)
            {
                if (_elementos.Count >= LimitesUnionPdf.MaximoFicheros)
                {
                    errores.Add(Textos["UnirErrorMaximoFicheros", LimitesUnionPdf.MaximoFicheros]);
                    break;
                }

                if (archivo.Size > LimitesArchivoSubido.TamanoMaximoBytes)
                {
                    errores.Add(Textos["UnirErrorDemasiadoGrande", archivo.Name, LimiteSubidaMb]);
                    continue;
                }

                if (_elementos.Where(e => e.Archivo is not null).Sum(e => e.Archivo!.TamanoBytes) + archivo.Size > LimitesUnionPdf.TamanoMaximoTotalBytes)
                {
                    errores.Add(Textos["UnirErrorTotalGrande", LimitesUnionPdf.TamanoMaximoTotalMb]);
                    break;
                }

                var error = await AnadirSubidoAsync(archivo);
                if (error is not null)
                    errores.Add(error);
            }
        }
        finally
        {
            _cargando = false;
        }

        _error = errores.Count == 0 ? null : string.Join(" ", errores);
    }

    /// <summary>Guarda el PDF en el almacén temporal y lo lee una vez para contar sus páginas y ver si lleva firma. <c>null</c> si entró.</summary>
    private async Task<string?> AnadirSubidoAsync(IBrowserFile archivo)
    {
        ArchivoTemporal? guardado = null;
        try
        {
            guardado = await _almacen!.GuardarAsync(
                () => archivo.OpenReadStream(LimitesArchivoSubido.TamanoMaximoBytes, _ciclo.Token),
                archivo.Size, LimitesArchivoSubido.TamanoMaximoBytes, cancellationToken: _ciclo.Token);

            Result<InfoPdfParaUnir> info;
            ReservaMemoriaDocumentos? reserva = await PresupuestoMemoria.ReservarAsync(guardado.TamanoBytes, _ciclo.Token);
            try
            {
                var contenido = await _almacen.LeerAsync(guardado, _ciclo.Token);
                info = await Mediator.Send(new PrepararUnionPdfQuery(contenido, archivo.Name), _ciclo.Token);
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

            if (info.EsFallido)
            {
                _almacen.Eliminar(guardado);
                return TextoDeEntrada(info.Error.Codigo, archivo.Name);
            }

            _elementos.Add(new ElementoUnion(archivo.Name, null, guardado, info.Valor.NumeroPaginas, info.Valor.EstaFirmado));
            return null;
        }
        catch (ArchivoTemporalDemasiadoGrandeException)
        {
            return Textos["UnirErrorDemasiadoGrande", archivo.Name, LimiteSubidaMb];
        }
        catch (Exception ex) when (ex is CuotaSubidaMasivaSuperadaException or PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException)
        {
            if (guardado is not null)
                _almacen!.Eliminar(guardado);
            return Textos["ErrorSinEspacio"];
        }
    }

    private void Mover(int posicion, int salto)
    {
        var destino = posicion + salto;
        if (_uniendo || posicion < 0 || posicion >= _elementos.Count || destino < 0 || destino >= _elementos.Count)
            return;

        OlvidarFirmas();
        (_elementos[posicion], _elementos[destino]) = (_elementos[destino], _elementos[posicion]);
    }

    private void Quitar(int posicion)
    {
        if (_uniendo || posicion < 0 || posicion >= _elementos.Count)
            return;

        OlvidarFirmas();
        if (_elementos[posicion].Archivo is { } archivo)
            _almacen?.Eliminar(archivo);
        _elementos.RemoveAt(posicion);
    }

    /// <summary>Un cambio en la lista invalida el aviso de firmas y su confirmación: la huella era de la lista anterior.</summary>
    private void OlvidarFirmas()
    {
        _firmasPendientes = [];
        _huellaFirmas = null;
    }

    /// <param name="firmasConfirmadas">La huella del aviso de firmas que la persona acepta; <c>null</c> al unir sin aviso previo.</param>
    private async Task UnirAsync(string? firmasConfirmadas)
    {
        if (_uniendo || !PuedeUnir || _almacen is null || _empresaId is not { } empresaId || _tenantEsperadoId is not { } tenantId)
            return;

        _uniendo = true;
        _error = null;
        OlvidarFirmas();
        // Capturado aquí: Dispose desecha el origen del token, y la descarga tiene que poder cortarse después.
        var token = _ciclo.Token;
        // El PDF unido trae su reserva del presupuesto de memoria y esta pantalla lo retiene hasta que la descarga termina:
        // se desecha en el finally, una sola vez, también si la descarga falla, se cancela o lanza.
        ResultadoUnionPdf? resultado = null;
        try
        {
            // Lo que se envía, en el orden de la lista: la posición de cada entrada es la de su elemento aquí.
            var enviados = _elementos.ToList();
            var subidos = enviados.Where(e => e.Archivo is not null).Sum(e => e.Archivo!.TamanoBytes);
            // Una sola espera por el total de lo subido (esperar reteniendo otra reserva bloquea a las demás operaciones, ver
            // IPresupuestoMemoriaDocumentos.TryReservar); si hay que retener, se reduce a lo de las subidas retenidas.
            var reservaSubidos = subidos > 0 ? await PresupuestoMemoria.ReservarAsync(subidos, _ciclo.Token) : null;
            try
            {
                var entradas = new List<EntradaUnionPdf>(enviados.Count);
                foreach (var elemento in enviados)
                {
                    entradas.Add(elemento.DocumentoId is { } documentoId
                        ? new EntradaUnionDocumento(documentoId)
                        : new EntradaUnionSubida(await _almacen.LeerAsync(elemento.Archivo!, _ciclo.Token), elemento.Nombre));
                }

                var respuesta = await PuertaAccesoDatos.EjecutarAsync(
                    () => Mediator.Send(new UnirPdfQuery(entradas, tenantId, empresaId, firmasConfirmadas), _ciclo.Token));
                if (respuesta.EsFallido)
                {
                    _error = MensajeDeOperacion(respuesta.Error);
                    return;
                }

                resultado = respuesta.Valor;
            }
            catch (EntradaPdfRetenidaException retenida) when (reservaSubidos is not null)
            {
                // El proceso aparte que recibió esos PDF no consta terminado y puede seguir referenciándolos: lo de las subidas
                // que tenía no se devuelve. Lo de las demás (una inspección que no es la suya, o un Documento) sí, como en el
                // handler: retenerlo inmovilizaría presupuesto compartido sin nada que lo use. Sin posiciones, se retiene todo.
                var retenidos = BytesSubidosRetenidos(enviados, retenida);
                if (retenidos > 0)
                {
                    reservaSubidos.ReducirA(retenidos);
                    EntradaPdfRetenidaException.Retener(reservaSubidos);
                    reservaSubidos = null;
                }
                throw;
            }
            finally
            {
                reservaSubidos?.Dispose();
            }

            switch (resultado.Estado)
            {
                case EstadoUnionPdf.FirmasSinConfirmar:
                    _firmasPendientes = resultado.Posiciones;
                    _huellaFirmas = resultado.HuellaFirmas;
                    break;
                case EstadoUnionPdf.EntradaRechazada:
                    _error = TextoDeEntrada(
                        resultado.CodigoRechazo!, NombreEnPosicion(resultado.Posiciones[0]), EsDocumentoEnPosicion(resultado.Posiciones[0]));
                    break;
                default:
                    await DescargarAsync(resultado, token);
                    break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // La pantalla se fue: no queda nadie a quien avisar. La reserva del PDF unido la devuelve el finally.
        }
        catch (Exception ex) when (ex is PresupuestoMemoriaAgotadoException or EntradaPdfRetenidaException)
        {
            _error = Textos["ErrorSinEspacio"];
        }
        catch (Exception ex) when (ErroresLecturaSensible.EsMapeable(ex))
        {
            // Sin constancia del acceso no se entrega el PDF (falla en cerrado): se dice y se puede repetir.
            Logger.LogWarning(ex, "Unir PDF no pudo registrar el acceso a Documentos; no se entrega el PDF.");
            _error = Textos["UnirErrorSinConstancia"];
        }
        finally
        {
            _uniendo = false;
            resultado?.Dispose();
        }
    }

    /// <summary>Bytes subidos cuyos PDF tenía el proceso que no consta terminado, según las posiciones que indica <paramref name="retenida"/>.</summary>
    private static long BytesSubidosRetenidos(IReadOnlyList<ElementoUnion> enviados, EntradaPdfRetenidaException retenida) =>
        enviados.Select((elemento, posicion) => (elemento, posicion))
            .Where(x => x.elemento.Archivo is not null && retenida.Retiene(x.posicion))
            .Sum(x => x.elemento.Archivo!.TamanoBytes);

    /// <summary>
    /// Lo que se espera a que el navegador lea el PDF unido. Es el plazo por defecto de las llamadas JS de Blazor, que pasar un
    /// token propio anula: sin él, un navegador que no tira del flujo retendría la reserva mientras viva el circuito.
    /// </summary>
    private static readonly TimeSpan PlazoDescarga = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Entrega el PDF unido al navegador como flujo. Vuelve cuando el navegador lo ha leído entero, ha fallado, pasa
    /// <see cref="PlazoDescarga"/> o la pantalla se desecha (<paramref name="token"/>): hasta entonces el flujo apunta a
    /// <see cref="ResultadoUnionPdf.Contenido"/> y quien llama mantiene su reserva.
    /// </summary>
    private async Task DescargarAsync(ResultadoUnionPdf resultado, CancellationToken token)
    {
        // Al vencer el plazo o desecharse la pantalla, la espera de interop se corta y quien llama devuelve la reserva, aunque
        // Blazor quizá siga transfiriendo el MemoryStream un instante. Se acepta: la reserva es una cuenta del presupuesto, no
        // el búfer, y ese búfer (el PDF unido, con su tope) deja de estar referenciado en cuanto Blazor suelta el flujo.
        using var vencimiento = new CancellationTokenSource(PlazoDescarga, Reloj);
        using var plazo = CancellationTokenSource.CreateLinkedTokenSource(token, vencimiento.Token);
        try
        {
            _modulo ??= await JS.InvokeAsync<IJSObjectReference>("import", plazo.Token, "./js/descarga-pdf-unido.js");
            using var referencia = new DotNetStreamReference(new MemoryStream(resultado.Contenido!, writable: false));
            await _modulo.InvokeVoidAsync("descargarPdf", plazo.Token, resultado.NombreArchivo, referencia);
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && vencimiento.IsCancellationRequested)
        {
            Logger.LogWarning("Unir PDF: el navegador no leyó el PDF unido en {Plazo}; se suelta.", PlazoDescarga);
            _error = Textos["UnirErrorDescarga"];
            return;
        }
        catch (JSException ex)
        {
            Logger.LogWarning(ex, "Unir PDF no pudo iniciar la descarga");
            _error = Textos["UnirErrorDescarga"];
            return;
        }

        _nombreUnido = resultado.NombreArchivo!;
        _paginasUnido = resultado.NumeroPaginas;
        _unido = true;
        RetirarSubidos();
    }

    private void RetirarSubidos()
    {
        foreach (var elemento in _elementos)
        {
            if (elemento.Archivo is { } archivo)
                _almacen?.Eliminar(archivo);
        }

        _elementos.Clear();
    }

    private Task ReiniciarAsync()
    {
        RetirarSubidos();
        _unido = false;
        _error = null;
        OlvidarFirmas();
        _nombreUnido = string.Empty;
        _paginasUnido = 0;
        return Task.CompletedTask;
    }

    private string NombreEnPosicion(int posicion) =>
        posicion >= 0 && posicion < _elementos.Count ? _elementos[posicion].Nombre : Textos["UnirElPdf"];

    private bool EsDocumentoEnPosicion(int posicion) =>
        posicion >= 0 && posicion < _elementos.Count && _elementos[posicion].DocumentoId is not null;

    /// <summary>
    /// El texto del fallo de un PDF concreto, con su nombre. Un Documento almacenado puede pasar del tope de una subida
    /// (10 MB) y solo se rechaza por pasar del total de la unión, así que el tope que se cita depende del origen.
    /// </summary>
    private string TextoDeEntrada(string codigo, string nombre, bool esDocumento = false) => codigo switch
    {
        ErroresUnirPdf.ConContrasena => Textos["UnirErrorContrasena", nombre],
        ErroresUnirPdf.NoEsPdf => Textos["UnirErrorNoEsPdf", nombre],
        ErroresUnirPdf.DemasiadoGrande => Textos["UnirErrorDemasiadoGrande", nombre,
            esDocumento ? LimitesUnionPdf.TamanoMaximoTotalMb : LimiteSubidaMb],
        ErroresUnirPdf.DemasiadasPaginas => Textos["UnirErrorDemasiadasPaginas", nombre, LimitesUnionPdf.MaximoPaginasTotales],
        ErroresUnirPdf.DemasiadoComplejo => Textos["UnirErrorDemasiadoComplejo", nombre],
        ErroresUnirPdf.DocumentoNoDisponible => Textos["UnirErrorDocumentoNoDisponible", nombre],
        ErroresUnirPdf.ArchivoNoDisponible => Textos["UnirErrorArchivoNoDisponible", nombre],
        _ => Textos["UnirErrorIlegible", nombre],
    };

    private string MensajeDeOperacion(Error error) => error.Codigo switch
    {
        ErroresUnirPdf.TenantCruzado => Textos["UnirErrorTenantCambiado"],
        ErroresUnirPdf.EmpresaNoGestionable => Textos["UnirErrorEmpresa"],
        ErroresUnirPdf.NumeroDeFicherosInvalido => Textos["UnirErrorNumeroFicheros", LimitesUnionPdf.MinimoFicheros, LimitesUnionPdf.MaximoFicheros],
        ErroresUnirPdf.TotalDemasiadoGrande => Textos["UnirErrorTotalGrande", LimitesUnionPdf.TamanoMaximoTotalMb],
        ErroresUnirPdf.TotalDemasiadasPaginas => Textos["UnirErrorTotalPaginas", LimitesUnionPdf.MaximoPaginasTotales],
        ErroresUnirPdf.RolSinAcceso => Textos["UnirErrorRol"],
        ErroresUnirPdf.SesionPrivilegiadaSinLectura => Textos["UnirErrorSesionPrivilegiada"],
        ErroresUnirPdf.SinEspacio => Textos["ErrorSinEspacio"],
        ErroresUnirPdf.NoDisponible => Textos["NoDisponibleDescripcion"],
        _ => Textos["UnirErrorGeneral"],
    };

    private static string Texto(Guid? id) => id?.ToString() ?? string.Empty;

    private static Guid? Identificador(string valor) => Guid.TryParse(valor, out var id) ? id : null;

    public void Dispose()
    {
        _desechado = true;
        _ciclo.Cancel();
        _almacen?.Dispose();
        _ciclo.Dispose();
        if (_modulo is not null)
            _ = _modulo.DisposeAsync().AsTask().ContinueWith(_ => { }, TaskScheduler.Default);
    }
}

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

namespace CaeManager.Web.Features.HerramientasPdf.Pages;

/// <summary>
/// Dividir un PDF con varios documentos: elegir el PDF, marcar los cortes, un paso por documento, revisar y crear. La
/// detección es local y solo sugiere; cada alta pasa por <see cref="CrearDocumentosDesdeDivisionCommand"/>, que delega en
/// el alta normal. La Empresa gestionada no se elige aquí: sale del selector de la barra lateral, y sin selección no se
/// puede subir nada.
///
/// El original vive en el almacén temporal del circuito, no en memoria, y desaparece al crear o al salir: no se conserva.
/// </summary>
public partial class DividirPdf : CaeManager.Web.Components.PaginaInteractiva, IDisposable
{
    /// <summary>Ancho con el que se rasteriza: el de «Grandes», que las otras dos escalan.</summary>
    private const int AnchoMiniaturaPixeles = 168;

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
    [Inject] private ILogger<DividirPdf> Logger { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosHerramientasPdf> Textos { get; set; } = default!;

    private readonly CancellationTokenSource _ciclo = new();
    private readonly Dictionary<int, string> _miniaturas = [];

    private bool _inicializando = true;
    private bool _disponible;
    private bool _soloLectura;
    private bool _sinEmpresa;
    private bool _selectorVisible;
    private bool _cargando;
    private bool _creando;
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

        if (!_sinEmpresa)
        {
            _empresas = await PuertaAccesoDatos.EjecutarAsync(
                () => Mediator.Send(new ObtenerEmpresasParaSelectorQuery(), _ciclo.Token));
            _empresaId = _empresas.Count == 1 ? _empresas[0].Id : null;
        }

        _inicializando = false;
    }

    private void ElegirEmpresa(string valor) => _empresaId = Identificador(valor);

    private void ElegirTamano(string valor) =>
        _tamanoPaginas = int.TryParse(valor, CultureInfo.InvariantCulture, out var tamano) ? tamano : _tamanoPaginas;

    private async Task CargarArchivoAsync(InputFileChangeEventArgs evento)
    {
        if (_almacen is null || _empresaId is not { } empresaId || _tenantEsperadoId is null)
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
            using (await PresupuestoMemoria.ReservarAsync(guardado.TamanoBytes, _ciclo.Token))
            {
                var contenido = await _almacen.LeerAsync(guardado, _ciclo.Token);
                preparacion = await Mediator.Send(new PrepararDivisionPdfQuery(contenido, empresaId), _ciclo.Token);
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
        catch (Exception ex) when (ex is CuotaSubidaMasivaSuperadaException or PresupuestoMemoriaAgotadoException)
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

    private string ClaveVolverTramo => _indiceTramo == 0 ? "VolverADividir" : "Anterior";

    private static Action<ChangeEventArgs> AlMarcarOmitido(AsignacionTramo asignacion) =>
        e => asignacion.Omitido = e.Value is true;

    private static Action<ChangeEventArgs> AlMarcarSinCaducidad(AsignacionTramo asignacion) =>
        e => asignacion.SinCaducidad = e.Value is true;

    private void AlternarCorte(int pagina) => _division?.AlternarCorte(pagina);

    private void ConfirmarPropuestas() => _division?.ConfirmarPropuestas();

    private void IrAAsignar()
    {
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
            using (await PresupuestoMemoria.ReservarAsync(_original.TamanoBytes, _ciclo.Token))
            {
                var contenido = await _almacen.LeerAsync(_original, _ciclo.Token);
                creacion = await Mediator.Send(
                    new CrearDocumentosDesdeDivisionCommand(contenido, _nombreOriginal, tenantId, empresaId, tramos), _ciclo.Token);
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
        catch (PresupuestoMemoriaAgotadoException)
        {
            _error = Textos["ErrorSinEspacio"];
        }
        finally
        {
            _creando = false;
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
        RetirarOriginal();
        _division = null;
        _miniaturas.Clear();
        _resultados = [];
        _error = null;
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
        fecha?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;

    private static DateOnly? Fecha(string valor) =>
        DateOnly.TryParseExact(valor, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var fecha) ? fecha : null;

    public void Dispose()
    {
        _desechado = true;
        _ciclo.Cancel();
        _vistas?.Dispose();
        _almacen?.Dispose();
        _ciclo.Dispose();
    }
}

using CaeManager.Application.Centros.Queries.ObtenerCentrosParaSelector;
using CaeManager.Application.Clientes.Queries.ObtenerClientesParaSelector;
using CaeManager.Application.Common;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Reportes.Commands.RegistrarHistorialInforme;
using CaeManager.Application.Reportes.Queries;
using CaeManager.Application.Reportes.Queries.ObtenerHistorialInformes;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Reportes;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Reportes.Pages;

public partial class Reportes : ComponentBase
{
    private sealed record DefinicionInforme(string Id, string Titulo, string Descripcion);

    /// <summary>
    /// Lo que pedía «Generar vista previa» en el momento de pulsarlo. La vista
    /// previa, las descargas y el adjunto de «Enviar por Comunicaciones» salen
    /// de aquí y no de los filtros vigentes: antes, generar para un cliente
    /// empresarial y cambiar después de cliente dejaba en pantalla las filas
    /// del primero mientras «Descargar PDF» bajaba las del segundo.
    /// </summary>
    private sealed record InformeGenerado(string Tipo, Guid? ClienteId, Guid? CentroId, bool IncluirVigentes, DateTime GeneradoEn);

    internal const string TipoVigencia = "vigencia";
    internal const string TipoIncidencias = "incidencias";
    internal const string TipoAsignaciones = "asignaciones";

    /// <summary>
    /// "Incidencias" del mockup describe "vencidos, rechazos de portal y
    /// bloqueos del periodo" — ni rechazo de portal ni bloqueo de periodo
    /// existen como concepto de dominio en ningún sitio de la app, así que
    /// la descripción real solo promete lo que GenerarInformeVigenciaQuery
    /// puede entregar de verdad: vencidos y urgentes. Por el mismo motivo
    /// "Vigencia documental" no dice "documentos exigidos": la consulta lista
    /// los documentos que existen, no los que un requisito exige (un
    /// documento que falta no sale en el informe).
    /// </summary>
    private static readonly IReadOnlyList<DefinicionInforme> Informes =
    [
        new(TipoVigencia, "Vigencia documental", "Estado de los documentos de los trabajadores, con sus vencimientos."),
        new(TipoIncidencias, "Incidencias", "Solo los documentos vencidos o urgentes."),
        new(TipoAsignaciones, "Asignaciones activas", "Quién está autorizado en cada centro y desde cuándo.")
    ];

    private static readonly string[] MensajesGenerando =
        ["Reuniendo los documentos…", "Calculando estados a fecha de hoy…", "Componiendo el informe…"];

    /// <summary>
    /// Espera mínima deliberada (mismo orden que el mockup): la consulta es
    /// casi instantánea y sin ella la barra de progreso sería un parpadeo.
    /// </summary>
    private static readonly TimeSpan EsperaMinima = TimeSpan.FromMilliseconds(1400);

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private NavigationManager Navegacion { get; set; } = default!;

    // Deep-link desde Centro 360 ("Generar informe del centro") y, por
    // simetría, desde donde haga falta preseleccionar cliente/centro sin
    // reconstruir la pantalla — mismo patrón que /visitas?centroId=.
    // Cambiar el filtro en pantalla reescribe estos parámetros en la URL
    // (ver SincronizarUrl): recargar o compartir el enlace reproduce lo que
    // se ve, y «Quitar el filtro» no deja un clienteId viejo esperando al F5.
    [SupplyParameterFromQuery(Name = "clienteId")]
    public string? ClienteIdQuery { get; set; }

    [SupplyParameterFromQuery(Name = "centroId")]
    public string? CentroIdQuery { get; set; }

    private string _tipoInforme = TipoVigencia;
    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private bool _cargandoCentros;
    private string _clienteId = string.Empty;
    private string _centroId = string.Empty;
    private bool _incluirVigentes = true;

    private bool _generando;
    private bool _errorGeneracion;
    private bool _sinInforme;
    private bool _errorRegistroHistorial;
    private InformeGenerado? _generado;
    private InformeVigenciaDto? _informeVigencia;
    private InformeAsignacionesDto? _informeAsignaciones;

    private bool _cargandoHistorial = true;
    private bool _errorHistorial;
    private IReadOnlyList<HistorialInformeDto> _historial = [];
    private readonly Dictionary<Guid, string> _usuariosPorId = [];

    // Cada carga que escribe estado tras un await lleva su número de
    // solicitud; si al volver ya no es la última, la respuesta se descarta.
    // Sin esto, los centros de un cliente elegido antes y resuelto después
    // se ofrecían bajo el cliente elegido ahora, y una vista previa pedida
    // con los filtros anteriores se pintaba bajo los nuevos.
    private int _solicitudCentros;
    private int _solicitudInforme;
    private int _solicitudHistorial;

    private DefinicionInforme InformeActual => Informes.First(i => i.Id == _tipoInforme);
    private bool SinCliente => string.IsNullOrEmpty(_clienteId);
    private bool EsVigencia => _tipoInforme == TipoVigencia;

    private IReadOnlyList<OpcionBuscable> OpcionesCliente =>
        _clientesDisponibles.Select(c => new OpcionBuscable(c.Id.ToString(), c.RazonSocial)).ToList();

    protected override async Task OnInitializedAsync()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        if (Guid.TryParse(ClienteIdQuery, out var clienteId) && _clientesDisponibles.Any(c => c.Id == clienteId))
        {
            _clienteId = clienteId.ToString();
            await CargarCentrosAsync();

            if (Guid.TryParse(CentroIdQuery, out var centroId) && _centrosDisponibles.Any(c => c.Id == centroId))
                _centroId = centroId.ToString();
        }

        await CargarHistorialAsync();
    }

    private async Task CargarHistorialAsync()
    {
        var solicitud = ++_solicitudHistorial;
        _cargandoHistorial = true;
        _errorHistorial = false;
        StateHasChanged();

        try
        {
            var historial = await Mediator.Send(new ObtenerHistorialInformesQuery());
            if (solicitud != _solicitudHistorial) return;

            var idsFaltantes = historial.Select(h => h.GeneradoPorUsuarioId).Distinct()
                .Where(id => !_usuariosPorId.ContainsKey(id)).ToList();

            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                foreach (var id in idsFaltantes)
                {
                    var usuario = await UserManager.FindByIdAsync(id.ToString());
                    _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                }
            });
            if (solicitud != _solicitudHistorial) return;

            _historial = historial;
        }
        catch (Exception)
        {
            // Antes un fallo aquí se tragaba y el panel decía «Todavía no se ha
            // generado ningún informe», que es otra cosa.
            if (solicitud != _solicitudHistorial) return;
            _historial = [];
            _errorHistorial = true;
        }
        finally
        {
            if (solicitud == _solicitudHistorial)
                _cargandoHistorial = false;
        }
    }

    private string NombreUsuario(Guid id) => _usuariosPorId.GetValueOrDefault(id, "—");

    private void SeleccionarInforme(string tipo)
    {
        if (tipo == _tipoInforme) return;

        _tipoInforme = tipo;
        if (tipo == TipoIncidencias) _incluirVigentes = false;
        else if (tipo == TipoVigencia) _incluirVigentes = true;
        InvalidarVistaPrevia();
    }

    private async Task CambiarClienteAsync(string clienteId)
    {
        if (clienteId == _clienteId) return;

        _clienteId = clienteId;
        _centroId = string.Empty;
        InvalidarVistaPrevia();
        SincronizarUrl();
        await CargarCentrosAsync();
    }

    private Task QuitarFiltroClienteAsync() => CambiarClienteAsync(string.Empty);

    private void CambiarCentro(string centroId)
    {
        if (centroId == _centroId) return;

        _centroId = centroId;
        InvalidarVistaPrevia();
        SincronizarUrl();
    }

    private void CambiarIncluirVigentes() => InvalidarVistaPrevia();

    private async Task CargarCentrosAsync()
    {
        var solicitud = ++_solicitudCentros;
        _centrosDisponibles = [];

        if (ClienteIdActual is not { } clienteId)
        {
            _cargandoCentros = false;
            return;
        }

        _cargandoCentros = true;
        try
        {
            var centros = await Mediator.Send(new ObtenerCentrosParaSelectorQuery(clienteId));
            if (solicitud != _solicitudCentros) return;

            _centrosDisponibles = centros;
        }
        catch (Exception)
        {
            // Sin centros el informe sigue pudiéndose pedir para todo el
            // cliente empresarial; solo se pierde el filtro por centro.
            if (solicitud != _solicitudCentros) return;
            _centrosDisponibles = [];
        }
        finally
        {
            if (solicitud == _solicitudCentros)
                _cargandoCentros = false;
        }
    }

    /// <summary>
    /// Cualquier cambio de filtro deja sin valor la vista previa que hubiera
    /// —y la que estuviera en camino—: lo que se ve y lo que se descarga tiene
    /// que corresponder a los filtros que el usuario tiene delante.
    /// </summary>
    private void InvalidarVistaPrevia()
    {
        _solicitudInforme++;
        _generando = false;
        _errorGeneracion = false;
        _sinInforme = false;
        _errorRegistroHistorial = false;
        _generado = null;
        _informeVigencia = null;
        _informeAsignaciones = null;
    }

    private void SincronizarUrl()
    {
        var destino = Navegacion.GetUriWithQueryParameters(new Dictionary<string, object?>
        {
            ["clienteId"] = ClienteIdActual?.ToString(),
            ["centroId"] = CentroIdActual?.ToString()
        });

        if (destino != Navegacion.Uri)
            Navegacion.NavigateTo(destino, replace: true);
    }

    private Guid? ClienteIdActual => Guid.TryParse(_clienteId, out var id) ? id : null;
    private Guid? CentroIdActual => Guid.TryParse(_centroId, out var id) ? id : null;

    private bool TienePlan => _generado is not null && (_informeVigencia is not null || _informeAsignaciones is not null);

    private string EnlaceExportar(string extension)
    {
        var generado = _generado!;
        var baseRuta = generado.Tipo == TipoAsignaciones ? "/reportes/asignaciones" : "/reportes/vigencia";

        // Guid? no admite bindear una query string vacía ("clienteId=") —
        // el parámetro tiene que faltar del todo para que el minimal API lo
        // trate como null (bug real encontrado al verificar en navegador:
        // "clienteId=&centroId=" devolvía 400 Bad Request).
        var parametros = new List<string>();
        if (generado.ClienteId is { } clienteId) parametros.Add($"clienteId={clienteId}");
        if (generado.CentroId is { } centroId) parametros.Add($"centroId={centroId}");
        if (generado.Tipo != TipoAsignaciones) parametros.Add($"incluirVigentes={generado.IncluirVigentes.ToString().ToLowerInvariant()}");

        return parametros.Count == 0 ? $"{baseRuta}.{extension}" : $"{baseRuta}.{extension}?{string.Join('&', parametros)}";
    }

    private async Task GenerarVistaPreviaAsync()
    {
        InvalidarVistaPrevia();
        var solicitud = _solicitudInforme;
        var tipo = _tipoInforme;
        var clienteId = ClienteIdActual;
        var centroId = CentroIdActual;
        var incluirVigentes = tipo != TipoAsignaciones && _incluirVigentes;

        _generando = true;
        StateHasChanged();

        try
        {
            var vigencia = tipo == TipoAsignaciones
                ? Task.FromResult<InformeVigenciaDto?>(null)
                : Mediator.Send(new GenerarInformeVigenciaQuery(clienteId, centroId, incluirVigentes));
            var asignaciones = tipo == TipoAsignaciones
                ? Mediator.Send(new GenerarInformeAsignacionesQuery(clienteId, centroId))
                : Task.FromResult<InformeAsignacionesDto?>(null);

            await Task.WhenAll(vigencia, asignaciones, Task.Delay(EsperaMinima));
            if (solicitud != _solicitudInforme) return;

            _informeVigencia = await vigencia;
            _informeAsignaciones = await asignaciones;
            _generando = false;

            // Null = el cliente empresarial o el centro pedido cae fuera de la
            // cartera del usuario (ver GenerarInformeVigenciaQuery). El
            // selector ya viene acotado, así que solo pasa si la cartera
            // cambió desde que se cargó la página: no hay informe que enseñar
            // ni nada que registrar.
            if (_informeVigencia is null && _informeAsignaciones is null)
            {
                _sinInforme = true;
                return;
            }

            _generado = new InformeGenerado(tipo, clienteId, centroId, incluirVigentes, DateTime.Now);
        }
        catch (Exception)
        {
            if (solicitud != _solicitudInforme) return;
            _informeVigencia = null;
            _informeAsignaciones = null;
            _generado = null;
            _errorGeneracion = true;
            return;
        }
        finally
        {
            if (solicitud == _solicitudInforme)
                _generando = false;
        }

        await RegistrarEnHistorialAsync(solicitud, tipo, clienteId);
    }

    /// <summary>
    /// Anotar en el historial es posterior a tener la vista previa: si falla,
    /// el informe sigue siendo válido y se dice aparte, en el propio panel.
    /// </summary>
    private async Task RegistrarEnHistorialAsync(int solicitud, string tipo, Guid? clienteId)
    {
        StateHasChanged();

        var nombreCliente = clienteId is not null
            ? _clientesDisponibles.FirstOrDefault(c => c.Id == clienteId)?.RazonSocial
            : null;

        try
        {
            await Mediator.Send(new RegistrarHistorialInformeCommand(Informes.First(i => i.Id == tipo).Titulo, clienteId, nombreCliente));
        }
        catch (Exception)
        {
            if (solicitud == _solicitudInforme)
                _errorRegistroHistorial = true;
        }

        await CargarHistorialAsync();
    }

    private string TituloGenerado => _generado switch
    {
        { Tipo: TipoAsignaciones } => "Informe de asignaciones activas",
        { IncluirVigentes: true } => "Informe de vigencia documental",
        _ => "Informe de incidencias"
    };

    private string? NombreClienteGenerado => _generado?.ClienteId is { } id
        ? _clientesDisponibles.FirstOrDefault(c => c.Id == id)?.RazonSocial
        : null;

    private static string ContarFilas(int cantidad, string singular, string plural) =>
        cantidad == 1 ? $"1 {singular}" : $"{cantidad:N0} {plural}";

    // --- Enviar por Comunicaciones — adjunta el mismo PDF que "Descargar
    // PDF" a un mensaje nuevo, reutilizando RedactarMensajeDrawer (extraído
    // de Bandeja.razor) en vez de construir un compositor propio para esta
    // pantalla. ---

    private bool _composerVisible;
    private AdjuntoParaEnviarDto? _adjuntoInforme;

    private void AbrirEnviarPorComunicaciones()
    {
        if (!TienePlan) return;

        var bytes = _informeVigencia is { } iv
            ? ConstructorInformeArchivos.PdfVigencia(iv, _generado!.IncluirVigentes)
            : ConstructorInformeArchivos.PdfAsignaciones(_informeAsignaciones!);

        _adjuntoInforme = new AdjuntoParaEnviarDto($"{TituloGenerado}.pdf", "application/pdf", bytes);
        _composerVisible = true;
    }
}

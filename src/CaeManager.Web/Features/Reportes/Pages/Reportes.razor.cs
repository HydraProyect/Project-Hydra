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
    /// "Incidencias" del mockup describe "vencidos, rechazos de portal y
    /// bloqueos del periodo" — ni rechazo de portal ni bloqueo de periodo
    /// existen como concepto de dominio en ningún sitio de la app, así que
    /// la descripción real solo promete lo que GenerarInformeVigenciaQuery
    /// puede entregar de verdad: vencidos y urgentes.
    /// </summary>
    private static readonly IReadOnlyList<DefinicionInforme> Informes =
    [
        new("vigencia", "Vigencia documental", "Estado de todos los documentos exigidos, con vencimientos."),
        new("incidencias", "Incidencias", "Documentos vencidos o urgentes del alcance seleccionado."),
        new("asignaciones", "Asignaciones activas", "Quién está autorizado en cada centro y desde cuándo.")
    ];

    private static readonly string[] MensajesGenerando =
        ["Reuniendo documentos del periodo…", "Calculando estados a fecha de hoy…", "Componiendo el membrete…"];

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;

    // Deep-link desde Centro 360 ("Generar informe del centro") y, por
    // simetría, desde donde haga falta preseleccionar cliente/centro sin
    // reconstruir la pantalla — mismo patrón que /visitas?centroId=.
    [SupplyParameterFromQuery(Name = "clienteId")]
    public string? ClienteIdQuery { get; set; }

    [SupplyParameterFromQuery(Name = "centroId")]
    public string? CentroIdQuery { get; set; }

    private string _tipoInforme = "vigencia";
    private IReadOnlyList<ClienteSelectorDto> _clientesDisponibles = [];
    private IReadOnlyList<CentroSelectorDto> _centrosDisponibles = [];
    private string _clienteId = string.Empty;
    private string _centroId = string.Empty;
    private bool _incluirVigentes = true;

    private bool _generando;
    private InformeVigenciaDto? _informeVigencia;
    private InformeAsignacionesDto? _informeAsignaciones;

    private bool _cargandoHistorial = true;
    private IReadOnlyList<HistorialInformeDto> _historial = [];
    private readonly Dictionary<Guid, string> _usuariosPorId = [];

    private DefinicionInforme InformeActual => Informes.First(i => i.Id == _tipoInforme);
    private bool SinCliente => string.IsNullOrEmpty(_clienteId);

    private IReadOnlyList<OpcionBuscable> OpcionesCliente =>
        _clientesDisponibles.Select(c => new OpcionBuscable(c.Id.ToString(), c.RazonSocial)).ToList();

    protected override async Task OnInitializedAsync()
    {
        _clientesDisponibles = await Mediator.Send(new ObtenerClientesParaSelectorQuery());

        if (Guid.TryParse(ClienteIdQuery, out var clienteId) && _clientesDisponibles.Any(c => c.Id == clienteId))
        {
            await CambiarClienteAsync(clienteId.ToString());

            if (Guid.TryParse(CentroIdQuery, out var centroId) && _centrosDisponibles.Any(c => c.Id == centroId))
                _centroId = centroId.ToString();
        }

        await CargarHistorialAsync();
    }

    private async Task CargarHistorialAsync()
    {
        _cargandoHistorial = true;
        StateHasChanged();

        try
        {
            _historial = await Mediator.Send(new ObtenerHistorialInformesQuery());

            var idsFaltantes = _historial.Select(h => h.GeneradoPorUsuarioId).Distinct()
                .Where(id => !_usuariosPorId.ContainsKey(id)).ToList();

            await PuertaAccesoDatos.EjecutarAsync(async () =>
            {
                foreach (var id in idsFaltantes)
                {
                    var usuario = await UserManager.FindByIdAsync(id.ToString());
                    _usuariosPorId[id] = usuario?.NombreCompleto ?? usuario?.Email ?? "(usuario eliminado)";
                }
            });
        }
        catch (Exception)
        {
            _historial = [];
        }
        finally
        {
            _cargandoHistorial = false;
        }
    }

    private string NombreUsuario(Guid id) => _usuariosPorId.GetValueOrDefault(id, "—");

    private void SeleccionarInforme(string tipo)
    {
        _tipoInforme = tipo;
        _informeVigencia = null;
        _informeAsignaciones = null;
        if (tipo == "incidencias") _incluirVigentes = false;
        else if (tipo == "vigencia") _incluirVigentes = true;
    }

    private async Task CambiarClienteAsync(string clienteId)
    {
        _clienteId = clienteId;
        _centroId = string.Empty;
        _centrosDisponibles = Guid.TryParse(clienteId, out var id)
            ? await Mediator.Send(new ObtenerCentrosParaSelectorQuery(id))
            : [];
    }

    private Guid? ClienteIdActual => Guid.TryParse(_clienteId, out var id) ? id : null;
    private Guid? CentroIdActual => Guid.TryParse(_centroId, out var id) ? id : null;

    private bool TienePlan => _informeVigencia is not null || _informeAsignaciones is not null;

    private string EnlaceExportar(string extension)
    {
        var baseRuta = _tipoInforme == "asignaciones" ? "/reportes/asignaciones" : "/reportes/vigencia";

        // Guid? no admite bindear una query string vacía ("clienteId=") —
        // el parámetro tiene que faltar del todo para que el minimal API lo
        // trate como null (bug real encontrado al verificar en navegador:
        // "clienteId=&centroId=" devolvía 400 Bad Request).
        var parametros = new List<string>();
        if (ClienteIdActual is { } clienteId) parametros.Add($"clienteId={clienteId}");
        if (CentroIdActual is { } centroId) parametros.Add($"centroId={centroId}");
        if (_tipoInforme != "asignaciones") parametros.Add($"incluirVigentes={_incluirVigentes.ToString().ToLowerInvariant()}");

        return parametros.Count == 0 ? $"{baseRuta}.{extension}" : $"{baseRuta}.{extension}?{string.Join('&', parametros)}";
    }

    private async Task GenerarVistaPreviaAsync()
    {
        _generando = true;
        _informeVigencia = null;
        _informeAsignaciones = null;
        StateHasChanged();

        try
        {
            // El propio ProgresoConMensajes rota los mensajes; esta espera
            // mínima es deliberada (mismo tiempo que el mockup, ~3.6s) para
            // que la barra de progreso se perciba real y no un parpadeo —
            // la consulta en sí es casi instantánea.
            var tarea = _tipoInforme == "asignaciones"
                ? GenerarAsignacionesAsync()
                : GenerarVigenciaAsync();

            await Task.WhenAll(tarea, Task.Delay(1400));

            var nombreCliente = ClienteIdActual is not null
                ? _clientesDisponibles.FirstOrDefault(c => c.Id == ClienteIdActual)?.RazonSocial
                : null;

            await Mediator.Send(new RegistrarHistorialInformeCommand(InformeActual.Titulo, ClienteIdActual, nombreCliente));
            await CargarHistorialAsync();
        }
        finally
        {
            _generando = false;
        }
    }

    private async Task GenerarVigenciaAsync() =>
        _informeVigencia = await Mediator.Send(new GenerarInformeVigenciaQuery(ClienteIdActual, CentroIdActual, _incluirVigentes));

    private async Task GenerarAsignacionesAsync() =>
        _informeAsignaciones = await Mediator.Send(new GenerarInformeAsignacionesQuery(ClienteIdActual, CentroIdActual));

    // --- Enviar por Comunicaciones — adjunta el mismo PDF que "Descargar
    // PDF" a un mensaje nuevo, reutilizando RedactarMensajeDrawer (extraído
    // de Bandeja.razor esta misma sesión) en vez de construir un compositor
    // propio para esta pantalla. ---

    private bool _composerVisible;
    private AdjuntoParaEnviarDto? _adjuntoInforme;

    private void AbrirEnviarPorComunicaciones()
    {
        if (!TienePlan) return;

        var bytes = _informeVigencia is { } iv
            ? ConstructorInformeArchivos.PdfVigencia(iv, _incluirVigentes)
            : ConstructorInformeArchivos.PdfAsignaciones(_informeAsignaciones!);

        _adjuntoInforme = new AdjuntoParaEnviarDto($"{InformeActual.Titulo}.pdf", "application/pdf", bytes);
        _composerVisible = true;
    }
}

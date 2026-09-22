using CaeManager.Application.Operaciones.IncorporacionCartera;
using CaeManager.Application.Operaciones.IncorporacionCartera.Commands;
using CaeManager.Application.Operaciones.IncorporacionCartera.Queries;
using CaeManager.Domain.Common;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Layout;
using CaeManager.Web.Features.IncorporacionCartera.Recursos;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;

namespace CaeManager.Web.Features.IncorporacionCartera.Components;

/// <summary>
/// Aviso emergente, no modal, con las solicitudes de incorporación a cartera
/// pendientes del Operador CAE, para que cualquier Coordinador CAE las acepte
/// o rechace sin salir de donde está. No es una notificación por persona: lee
/// el estado vivo de las solicitudes al cargar y cada
/// <see cref="IntervaloRefresco"/>, así que cuando un Coordinador CAE resuelve
/// una, desaparece del aviso de los demás en el siguiente refresco (y de
/// inmediato si intentan resolverla, porque el Command responde YaResuelta).
/// Qué solicitudes ve y cuáles puede resolver lo decide la Query; el aviso
/// solo omite las que no puede resolver (las suyas propias).
/// «Más tarde» pospone las que hay ahora; una solicitud nueva vuelve a abrirlo.
/// </summary>
public partial class AvisoSolicitudesCartera : ComponentBase, IDisposable
{
    private const int MaximoEnAviso = 3;
    private static readonly TimeSpan IntervaloRefresco = TimeSpan.FromSeconds(60);

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private ToastService Toasts { get; set; } = default!;
    [Inject] private NavigationManager Navigation { get; set; } = default!;
    [Inject] private IStringLocalizer<TextosIncorporacionCartera> Textos { get; set; } = default!;
    [Inject] private ILogger<ExcepcionDeCircuitoDesconectado> Logger { get; set; } = default!;

    private IReadOnlyList<SolicitudIncorporacionCarteraDto> _pendientes = [];
    private readonly HashSet<Guid> _pospuestas = [];
    private Guid? _enCurso;
    private CancellationTokenSource? _cancelacion;

    private IReadOnlyList<SolicitudIncorporacionCarteraDto> Visibles =>
        _pendientes.Where(s => s.PuedeResolver && !_pospuestas.Contains(s.Id)).ToList();

    private bool EnLaBandeja =>
        Navigation.ToBaseRelativePath(Navigation.Uri).Split('?', '#')[0]
            .Equals(RutasIncorporacionCartera.Bandeja.TrimStart('/'), StringComparison.OrdinalIgnoreCase);

    protected override async Task OnInitializedAsync()
    {
        Navigation.LocationChanged += AlCambiarDeRuta;
        await CargarAsync();
        IniciarRefresco();
    }

    private async Task CargarAsync()
    {
        // Mismo mecanismo que NotificacionesPopup: el circuito puede
        // desconectarse con la carga en vuelo (ExcepcionDeCircuitoDesconectado,
        // REC-166). No queda nadie esperando el aviso; solo se registra.
        try
        {
            var resultado = await Mediator.Send(new ObtenerSolicitudesIncorporacionCarteraQuery(SoloPendientes: true));

            // Sin permiso (otro rol en el workspace activo) o sin Operador CAE
            // de origen: el aviso no se muestra. Falla cerrado, sin ruido.
            _pendientes = resultado.EsExitoso && resultado.Valor.EsCoordinadorCae
                ? resultado.Valor.Pendientes
                : [];
        }
        catch (Exception ex) when (ExcepcionDeCircuitoDesconectado.Es(ex))
        {
            Logger.LogWarning(ex, "AvisoSolicitudesCartera descartó una excepción de desconexión de circuito: {TipoExcepcion} — {Mensaje}",
                ex.GetType().Name, ex.Message);
        }
    }

    private void IniciarRefresco()
    {
        _cancelacion = new CancellationTokenSource();
        var token = _cancelacion.Token;

        _ = Task.Run(async () =>
        {
            using var reloj = new PeriodicTimer(IntervaloRefresco);
            try
            {
                while (await reloj.WaitForNextTickAsync(token))
                {
                    await InvokeAsync(async () =>
                    {
                        // No se refresca con una resolución en curso: la
                        // recarga posterior a esa resolución ya lo hace.
                        if (_enCurso is not null)
                            return;

                        await CargarAsync();
                        StateHasChanged();
                    });
                }
            }
            catch (OperationCanceledException)
            {
                // Componente desechado.
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "AvisoSolicitudesCartera detuvo su refresco periódico: {TipoExcepcion} — {Mensaje}",
                    ex.GetType().Name, ex.Message);
            }
        }, token);
    }

    private Task AceptarAsync(SolicitudIncorporacionCarteraDto solicitud) =>
        EjecutarAsync(solicitud.Id, new AceptarSolicitudIncorporacionCarteraCommand(solicitud.Id),
            Textos["ToastAceptada", solicitud.NombreSolicitante, solicitud.NombreEmpresa]);

    private Task RechazarAsync(SolicitudIncorporacionCarteraDto solicitud) =>
        EjecutarAsync(solicitud.Id, new RechazarSolicitudIncorporacionCarteraCommand(solicitud.Id), Textos["ToastRechazada"]);

    private async Task EjecutarAsync(Guid solicitudId, IRequest<Result> command, string textoExito)
    {
        if (_enCurso is not null)
            return;

        _enCurso = solicitudId;
        try
        {
            var resultado = await Mediator.Send(command);
            if (resultado.EsFallido)
                Toasts.Mostrar(TextosIncorporacionCartera.MensajeDeError(Textos, resultado.Error), TonoToast.Error);
            else
                Toasts.Mostrar(textoExito, TonoToast.Exito);

            await CargarAsync();
        }
        finally
        {
            _enCurso = null;
        }
    }

    private void Posponer()
    {
        foreach (var solicitud in Visibles)
            _pospuestas.Add(solicitud.Id);
    }

    private void AlCambiarDeRuta(object? sender, LocationChangedEventArgs e) =>
        _ = InvokeAsync(StateHasChanged);

    public void Dispose()
    {
        Navigation.LocationChanged -= AlCambiarDeRuta;
        _cancelacion?.Cancel();
        _cancelacion?.Dispose();
        _cancelacion = null;
    }
}

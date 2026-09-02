using CaeManager.Application.Configuracion;
using CaeManager.Application.Configuracion.Commands.ActualizarEstadoAutomatizacion;
using CaeManager.Application.Configuracion.Queries.ObtenerEstadoAutomatizaciones;
using CaeManager.Infrastructure.Alertas;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Infrastructure.VigilanciaNormativa;
using CaeManager.Web.Components.DesignSystem;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Features.Configuracion.Components;

public partial class AutomatizacionesPanel : ComponentBase
{
    [Inject] private IOptions<Microsoft365GraphOptions> OpcionesMicrosoft365 { get; set; } = default!;
    [Inject] private IOptions<WhatsAppCloudApiOptions> OpcionesWhatsApp { get; set; } = default!;
    [Inject] private IOptions<AlertasPorCorreoOptions> OpcionesAlertasPorCorreo { get; set; } = default!;
    [Inject] private IOptions<VigilanciaNormativaBoeOptions> OpcionesVigilanciaNormativaBoe { get; set; } = default!;

    private bool _cargando = true;
    private bool _errorCarga;
    private IReadOnlyList<AutomatizacionDto> _trabajos = [];
    private readonly HashSet<string> _actualizando = [];

    /// <summary>
    /// Un trabajo condicional (M365/WhatsApp/alertas por correo/BOE) cuyo
    /// hosted service no está registrado nunca deja fila en
    /// <c>EstadosAutomatizacion</c> — "Sin datos" en la tabla sería
    /// indistinguible entre "todavía no ejecutó" y "nunca va a ejecutar
    /// porque no está configurado" (A-06, salud de plataforma). Mismas
    /// condiciones que <c>InfrastructureServiceCollectionExtensions</c> usa
    /// para decidir si registra cada <c>AddHostedService</c> — no se
    /// duplican reglas nuevas, se lee la misma fuente.
    /// </summary>
    private bool EstaConfigurado(string trabajoId) => trabajoId switch
    {
        CatalogoAutomatizaciones.IngestaCorreoM365 => OpcionesMicrosoft365.Value.EstaConfigurado,
        CatalogoAutomatizaciones.IngestaWhatsApp => OpcionesWhatsApp.Value.EstaConfigurado,
        CatalogoAutomatizaciones.AlertasVencimientoDiarias => OpcionesAlertasPorCorreo.Value.Activo,
        CatalogoAutomatizaciones.VigilanciaNormativaBoe => OpcionesVigilanciaNormativaBoe.Value.Activa,
        _ => true, // Vigilancia de visitas urgentes: sin interruptor de configuración, siempre registrado.
    };

    protected override Task OnInitializedAsync() => CargarAsync();

    private async Task CargarAsync()
    {
        _cargando = true;
        _errorCarga = false;
        StateHasChanged();

        try
        {
            _trabajos = await Mediator.Send(new ObtenerEstadoAutomatizacionesQuery());
        }
        catch (Exception)
        {
            _errorCarga = true;
        }
        finally
        {
            _cargando = false;
        }
    }

    private async Task ConmutarAsync(AutomatizacionDto trabajo)
    {
        if (!_actualizando.Add(trabajo.Id)) return;
        StateHasChanged();

        try
        {
            var resultado = await Mediator.Send(new ActualizarEstadoAutomatizacionCommand(trabajo.Id, !trabajo.Activo));
            if (resultado.EsFallido)
            {
                ToastService.Mostrar(resultado.Error.Mensaje, TonoToast.Error);
                return;
            }

            await CargarAsync();
        }
        catch (Exception)
        {
            ToastService.Mostrar("No pudimos actualizar el estado. Intenta nuevamente en unos segundos.", TonoToast.Error);
        }
        finally
        {
            _actualizando.Remove(trabajo.Id);
            StateHasChanged();
        }
    }

    private static string FormatearUltimaEjecucion(DateTime? ultimaEjecucionUtc) =>
        ultimaEjecucionUtc is null ? "—" : ultimaEjecucionUtc.Value.ToLocalTime().ToString("dd/MM HH:mm");
}

using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;

namespace CaeManager.Web.Features.Dashboard.Pages;

public partial class Dashboard : ComponentBase
{
    private const int MaximoItemsAtencion = 5;
    private const int MaximoVisitasProximas = 3;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ActividadUsuarioService ActividadUsuario { get; set; } = default!;

    private KpisDashboardDto? _kpis;
    private IReadOnlyList<ItemBandejaDto> _requiereAtencion = [];
    private IReadOnlyList<ItemBandejaDto> _queLlegoSinVer = [];
    private IReadOnlyList<VisitaListaDto> _proximamente = [];
    private ProximoVencimientoDto? _proximoVencimiento;
    private PulsoEquipoDto? _pulso;
    private IReadOnlyList<ReclamacionSinRespuestaDto> _sinRespuesta = [];
    private bool _error;

    // "Requiere atención" reutiliza la Bandeja del gestor (docs/blueprints/OPERATIONAL-HOME.md
    // § 4) — visible a todos los roles salvo Cliente, mismo alcance que "Documentos que
    // requieren atención" tenía en el Dashboard anterior (_mostrarDocumentosAtencion).
    private bool _mostrarRequiereAtencion;

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>Saludo según la hora del servidor — sustituye el título estático "Dashboard" (ver DESIGN_SYSTEM.md, cabecera con jerarquía).</summary>
    private static string Saludo => DateTime.Now.Hour switch
    {
        < 12 => "Buenos días",
        < 20 => "Buenas tardes",
        _ => "Buenas noches"
    };

    private async Task CargarAsync()
    {
        _error = false;
        _kpis = null;
        StateHasChanged();

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            _mostrarRequiereAtencion = !estadoAutenticacion.User.IsInRole(Roles.Cliente);

            _kpis = await Mediator.Send(new ObtenerKpisDashboardQuery());

            if (!_kpis.SinCarteraAsignada)
            {
                var visitas = await Mediator.Send(new ObtenerVisitasQuery(
                    Busqueda: null, SoloActivas: true, NotificadoCliente: null, TamanoPagina: MaximoVisitasProximas));
                _proximamente = visitas.Elementos;

                if (_mostrarRequiereAtencion)
                {
                    var bandeja = await Mediator.Send(new ObtenerBandejaGestorQuery());
                    _requiereAtencion = [.. bandeja.Take(MaximoItemsAtencion)];

                    // Resuelto una única vez por circuito en MainLayout — aquí solo se lee el
                    // resultado ya cacheado (docs/blueprints/OPERATIONAL-HOME.md § 6, DDL-068).
                    var (ausente, desde) = await ActividadUsuario.RegistrarYEvaluarAsync(RendererInfo.IsInteractive);
                    if (ausente && desde is { } desdeValor)
                        _queLlegoSinVer = [.. bandeja.Where(i => i.CreadaEnUtc > desdeValor)];

                    // El próximo vencimiento solo aporta algo cuando la cola ya está vacía — con
                    // ítems pendientes que mostrar no hace falta ir a buscar qué viene después
                    // (DDL-071).
                    if (_requiereAtencion.Count == 0)
                        _proximoVencimiento = (await Mediator.Send(new ObtenerDesgloseDashboardQuery())).ProximoVencimiento;

                    _pulso = await Mediator.Send(new ObtenerPulsoEquipoQuery());

                    // Reclamaciones que salieron y siguen sin contestar. Aquí y
                    // no en el Action Center de Comunicaciones: ese es por
                    // conversación, y esto cruza hilos. DDL-007 mantiene sin
                    // congelar el contrato genérico del Action Center, así que
                    // esto es una sección propia, no un segundo consumidor de
                    // aquel componente.
                    _sinRespuesta = await Mediator.Send(new ObtenerReclamacionesSinRespuestaQuery());
                }
            }
        }
        catch (Exception)
        {
            _error = true;
        }
    }

    /// <summary>
    /// Estado de cierre verificado (DDL-071): con la cola de "Requiere atención" vacía se
    /// enuncia el hecho y el siguiente vencimiento por delante — "nada pendiente" solo
    /// tranquiliza si dice también qué viene después. Distinto del vacío por falta de
    /// cartera, que ya tiene su propia rama (04 § 6: un vacío mostrado como éxito sin
    /// verificar es engañoso).
    /// </summary>
    private string TextoCierreAtencion =>
        _proximoVencimiento is { } proximo
            ? $"Nada pendiente ahora mismo. Próximo vencimiento: {proximo.TipoDocumentoNombre} de {proximo.TrabajadorNombre}, el {proximo.FechaVencimiento:dd/MM/yyyy} ({TextoDias(proximo.DiasRestantes)})."
            : "Nada pendiente ahora mismo.";

    private static string TextoDias(int dias) => dias switch
    {
        0 => "hoy",
        1 => "en 1 día",
        _ => $"en {dias} días"
    };

    private static TonoBadge SlaDocumentalTono(int tasa) => tasa switch
    {
        >= 90 => TonoBadge.Exito,
        >= 70 => TonoBadge.Advertencia,
        _ => TonoBadge.Peligro
    };
}

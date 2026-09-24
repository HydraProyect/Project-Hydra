using System.Globalization;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Bandeja;
using CaeManager.Web.Services;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Identity;

namespace CaeManager.Web.Features.Dashboard.Pages;

/// <summary>
/// Inicio (SCREEN 01 de la auditoría de producto 2026-08-16, Parte XV) —
/// sustituye al Dashboard anterior en "/" (Dashboard.razor(.cs/.css)
/// eliminados). Trae dos piezas que el Dashboard antiguo no tenía:
/// "Requiere atención" agrupado por situación
/// (<see cref="ObtenerBandejaAgrupadaQuery"/>, hallazgo P-03) y "Pendiente
/// por plataforma" (<see cref="ObtenerPendientePorPlataformaQuery"/>,
/// hallazgo P-04).
/// </summary>
public partial class Inicio : ComponentBase, IDisposable
{
    private const int MaximoGruposAtencion = 5;
    private const int MaximoVisitasProximas = 3;

    [Inject] private IMediator Mediator { get; set; } = default!;
    [Inject] private AuthenticationStateProvider AuthenticationStateProvider { get; set; } = default!;
    [Inject] private NavigationManager NavigationManager { get; set; } = default!;
    [Inject] private ActividadUsuarioService ActividadUsuario { get; set; } = default!;
    [Inject] private UserManager<ApplicationUser> UserManager { get; set; } = default!;
    [Inject] private PuertaAccesoDatos PuertaAccesoDatos { get; set; } = default!;
    [Inject] private ITenantActual TenantActual { get; set; } = default!;

    /// <summary>
    /// Destino del aterrizaje del Gestor CAE de un Operador CAE externo (D-2 del
    /// piloto Outbound): su trabajo vive en los Tenants propietarios que opera,
    /// no en el Tenant de origen del Operador CAE.
    /// </summary>
    public const string RutaMiTrabajo = "/mi-trabajo";

    /// <summary>
    /// Roles de cuenta que autoriza la página Mi trabajo (<c>MiTrabajo.razor</c>,
    /// atributo <c>Authorize</c>). Solo se lleva allí a quien puede abrirla: un
    /// rol Consulta con cartera en otro Tenant acabaría en «acceso denegado».
    /// </summary>
    private static readonly string[] RolesMiTrabajo =
        [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae];

    private KpisDashboardDto? _kpis;

    /// <summary>
    /// El Context Workspace activo no tiene cartera del usuario, pero algún otro
    /// Tenant propietario autorizado sí. Solo se calcula cuando Inicio iba a
    /// decir «Sin cartera asignada».
    /// </summary>
    private bool _carteraEnOtroTenant;
    private BandejaAgrupadaDto? _bandejaAgrupada;
    private IReadOnlyList<PendientePorPlataformaDto> _pendientePorPlataforma = [];
    private IReadOnlyList<ItemBandejaDto> _queLlegoSinVer = [];
    private IReadOnlyList<VisitaListaDto> _proximamente = [];
    private ProximoVencimientoDto? _proximoVencimiento;
    private PulsoEquipoDto? _pulso;
    private IReadOnlyList<ReclamacionSinRespuestaDto> _sinRespuesta = [];
    private string? _nombrePila;
    private bool _error;
    private PerfilVocabularioTenant _perfilVocabulario = PerfilVocabularioTenant.ClienteDirecto;

    /// <summary>
    /// Instante en que terminó la carga que pintó lo que se está viendo. El
    /// mockup pone «Última actualización 09:12» junto a «Pendiente por
    /// plataforma»; aquí sube a la cabecera porque la pantalla entera se carga
    /// de una vez y el mismo minuto repetido por sección no es información
    /// nueva. Local, no UTC: es una hora que el Gestor CAE compara con su reloj.
    /// </summary>
    private DateTime? _actualizadoA;

    /// <summary>
    /// Corte de «qué llegó sin ver» (<see cref="ActividadUsuarioService"/>):
    /// la última actividad registrada ANTES de esta interacción. Se guarda para
    /// poder decir desde cuándo se cuenta, en vez de un «desde ayer» que el dato
    /// no sostiene — la ausencia puede ser de una hora o de una semana.
    /// </summary>
    private DateTime? _sinVerDesdeUtc;

    /// <summary>
    /// Se cancela al salir de la pantalla: las consultas en curso dejan de
    /// trabajar para nadie y ninguna respuesta tardía repinta un componente ya
    /// retirado. Mismo patrón que «Mi trabajo» (<c>/bandeja</c>), Empresas y
    /// DeteccionTrabajadores.
    /// </summary>
    private readonly CancellationTokenSource _ciclo = new();
    private bool _desechado;

    /// <summary>
    /// Número de la última carga. Cada carga captura el suyo ANTES del
    /// <c>await</c> y, al volver, solo escribe estado si sigue siendo la
    /// vigente. Sin esto, dos «Reintentar» seguidos dejaban que la respuesta
    /// lenta de la carga superada pisara el dashboard ya pintado, y que su fallo
    /// encendiera el estado de error de una carga que había ido bien.
    /// </summary>
    private int _cargaVigente;

    public void Dispose()
    {
        if (_desechado)
            return;

        _desechado = true;
        _ciclo.Cancel();
        _ciclo.Dispose();
    }

    /// <summary>La respuesta es de la pregunta vigente y la pantalla sigue viva.</summary>
    private bool EsVigente(int carga) => !_desechado && carga == _cargaVigente;

    /// <summary>
    /// Consultora gestiona la CAE de varias Empresas contratistas bajo un
    /// mismo Cliente titular — "Requiere atención" necesita el nivel
    /// Empresa→Trabajador para no mezclarlas en una sola lista. ClienteDirecto
    /// ("empresa final", DDL-072) ES la Empresa: ese nivel sería redundante,
    /// solo Trabajador.
    /// </summary>
    private bool AgruparPorEmpresa => _perfilVocabulario == PerfilVocabularioTenant.Consultora;

    // "Requiere atención" reutiliza la Bandeja del gestor — visible a todos
    // los roles salvo Cliente.
    private bool _mostrarRequiereAtencion;

    protected override Task OnInitializedAsync() => CargarAsync();

    /// <summary>
    /// El nombre de pila se resuelve aparte, tras el primer render, y NO
    /// dentro de CargarAsync — UserManager.GetUserAsync usa el mismo
    /// DbContext con ámbito de circuito que el resto de componentes del
    /// shell (MainLayout: SelectorClienteActivo, ContextWorkspace…), que
    /// también corren su propia inicialización en paralelo durante la carga
    /// inicial. Mover la llamada a OnAfterRenderAsync reduce la ventana pero
    /// no la cierra del todo — reproducido en vivo ("a second operation was
    /// started on this context instance", seguido de ObjectDisposedException
    /// al liberar un semáforo de PuertaAccesoDatos ya liberado por la
    /// carrera). El acceso directo a UserManager tiene que pasar por la
    /// puerta, igual que ya hace MainLayout.razor.cs con la suya — ver
    /// PuertaAccesoDatos.cs: "los accesos directos (UserManager en páginas y
    /// layout...) se envuelven en su sitio".
    /// </summary>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender || _desechado) return;

        // El token se lee ANTES del await, como en CargarAsync. Si la pantalla
        // se retira mientras se resuelve la autenticación, Dispose ya desechó
        // el CancellationTokenSource y leer _ciclo.Token después lanzaría
        // ObjectDisposedException, que aquí no recoge nadie: salir de Inicio
        // durante la carga del saludo dejaba un error en el circuito. Cancel()
        // corre antes que Dispose(), así que el token capturado llega ya
        // cancelado y la puerta corta sola.
        var token = _ciclo.Token;

        try
        {
            await ResolverSaludoAsync(token);
        }
        catch (OperationCanceledException)
        {
            // La pantalla se retiró mientras se resolvía el saludo. No hay nada
            // que pintar ni nada que avisar: el saludo sin nombre de pila es un
            // estado válido, no un fallo.
        }
    }

    private async Task ResolverSaludoAsync(CancellationToken token)
    {
        var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
        var nombrePila = await PuertaAccesoDatos.EjecutarAsync(async () =>
        {
            var usuario = await UserManager.GetUserAsync(estadoAutenticacion.User);
            // Identity.Name es el email de acceso (mismo dato que usa el enlace "Mi
            // firma" del topbar), no un nombre para mostrar; NombreCompleto es el
            // campo real (mismo origen que PestanaHistorial usa para "quién hizo
            // esto"). Sin NombreCompleto, se omite el nombre del saludo en vez de
            // caer en el email — "Buenos días, refri.gestorcae1@..." es peor que no
            // personalizar. Solo el nombre de pila, como en el mockup ("Buenos días,
            // Marta") — el nombre completo en un saludo de cabecera lee como un formulario.
            return usuario?.NombreCompleto is { Length: > 0 } nombreCompleto
                ? nombreCompleto.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
                : null;
        }, token);

        if (_desechado)
            return;

        if (nombrePila != _nombrePila)
        {
            _nombrePila = nombrePila;
            StateHasChanged();
        }
    }

    private string Saludo => _nombrePila is { Length: > 0 }
        ? $"{SaludoBase}, {_nombrePila}"
        : SaludoBase;

    private static string SaludoBase => DateTime.Now.Hour switch
    {
        < 12 => "Buenos días",
        < 20 => "Buenas tardes",
        _ => "Buenas noches"
    };

    private async Task CargarAsync()
    {
        if (_desechado)
            return;

        var carga = ++_cargaVigente;

        _error = false;
        _kpis = null;
        StateHasChanged();

        // Estado local de esta carga: nada se publica en los campos de la
        // pantalla hasta comprobar que esta sigue siendo la carga vigente. Una
        // carga superada que escribiera a medida que avanza dejaría el
        // dashboard mezclando dos respuestas distintas.
        var token = _ciclo.Token;
        BandejaAgrupadaDto? bandeja = null;
        IReadOnlyList<PendientePorPlataformaDto> plataformas = [];
        IReadOnlyList<ItemBandejaDto> sinVer = [];
        DateTime? sinVerDesde = null;
        IReadOnlyList<VisitaListaDto> proximamente = [];
        ProximoVencimientoDto? proximoVencimiento = null;
        PulsoEquipoDto? pulso = null;
        IReadOnlyList<ReclamacionSinRespuestaDto> sinRespuesta = [];
        var carteraEnOtroTenant = false;
        var activoEsOrigen = false;
        var irAMiTrabajo = false;

        try
        {
            var estadoAutenticacion = await AuthenticationStateProvider.GetAuthenticationStateAsync();
            var mostrarRequiereAtencion = !estadoAutenticacion.User.IsInRole(Roles.Cliente);
            var perfilVocabulario = await Mediator.Send(new ObtenerPerfilVocabularioActualQuery(), token);

            var kpis = await Mediator.Send(new ObtenerKpisDashboardQuery(), token);

            if (kpis.SinCarteraAsignada && RolesMiTrabajo.Any(estadoAutenticacion.User.IsInRole))
                (carteraEnOtroTenant, activoEsOrigen) = await ResolverCarteraEnOtroTenantAsync(token);

            if (carteraEnOtroTenant && activoEsOrigen)
            {
                // D-2: el Gestor CAE (o Coordinador CAE) de un Operador CAE externo
                // aterriza en Mi trabajo. Nada se publica: la pantalla se va.
                irAMiTrabajo = true;
                return;
            }

            if (!kpis.SinCarteraAsignada)
            {
                var visitas = await Mediator.Send(new ObtenerVisitasQuery(
                    Busqueda: null, SoloActivas: true, NotificadoCliente: null, TamanoPagina: MaximoVisitasProximas), token);
                proximamente = visitas.Elementos;

                plataformas = await Mediator.Send(new ObtenerPendientePorPlataformaQuery(), token);

                if (mostrarRequiereAtencion)
                {
                    bandeja = await Mediator.Send(new ObtenerBandejaAgrupadaQuery(), token);
                    var todosLosItems = bandeja.Grupos.SelectMany(g => g.Items).Concat(bandeja.SinGrupo).ToList();

                    // Resuelto una única vez por circuito en MainLayout — aquí solo se lee el
                    // resultado ya cacheado (docs/blueprints/OPERATIONAL-HOME.md § 6, DDL-068).
                    var (ausente, desde) = await ActividadUsuario.RegistrarYEvaluarAsync(RendererInfo.IsInteractive, token);
                    if (ausente && desde is { } desdeValor)
                    {
                        sinVer = [.. todosLosItems.Where(i => i.CreadaEnUtc > desdeValor)];
                        sinVerDesde = desdeValor;
                    }

                    // El próximo vencimiento solo aporta algo cuando la cola ya está vacía.
                    if (todosLosItems.Count == 0)
                        proximoVencimiento = (await Mediator.Send(new ObtenerDesgloseDashboardQuery(), token)).ProximoVencimiento;

                    pulso = await Mediator.Send(new ObtenerPulsoEquipoQuery(), token);
                    sinRespuesta = await Mediator.Send(new ObtenerReclamacionesSinRespuestaQuery(), token);
                }
            }

            if (!EsVigente(carga))
                return;

            _mostrarRequiereAtencion = mostrarRequiereAtencion;
            _perfilVocabulario = perfilVocabulario;
            _kpis = kpis;
            _carteraEnOtroTenant = carteraEnOtroTenant;
            _bandejaAgrupada = bandeja;
            _pendientePorPlataforma = plataformas;
            _queLlegoSinVer = sinVer;
            _sinVerDesdeUtc = sinVerDesde;
            _proximamente = proximamente;
            _proximoVencimiento = proximoVencimiento;
            _pulso = pulso;
            _sinRespuesta = sinRespuesta;
            _actualizadoA = DateTime.Now;
        }
        catch (Exception) when (!EsVigente(carga))
        {
            // Una carga superada que falla no es un error de la vigente: no
            // puede tapar su resultado con el estado de error.
        }
        catch (Exception)
        {
            _error = true;
        }
        finally
        {
            // Fuera del catch a propósito: la navegación no puede acabar
            // convertida en el estado de error de la pantalla. replace: Inicio
            // no fue un paso propio que «Atrás» deba devolver — volver a él
            // solo repetiría el salto.
            if (EsVigente(carga))
            {
                if (irAMiTrabajo)
                    NavigationManager.NavigateTo(RutaMiTrabajo, replace: true);
                else
                    StateHasChanged();
            }
        }
    }

    /// <summary>
    /// Si el usuario tiene cartera en algún Tenant propietario autorizado que no
    /// sea su Tenant de origen, y si el Context Workspace activo es ese Tenant de
    /// origen. Sin consulta nueva: la lista de Tenants autorizados es
    /// <see cref="ObtenerClientesAutorizadosQuery"/> (la misma que recorre Mi
    /// trabajo Gen2) y «sin cartera» por Tenant es
    /// <see cref="ClienteRiesgoDto.SinCarteraAsignada"/> de
    /// <see cref="ObtenerKpisGlobalesQuery"/>, que calcula cada Tenant con su
    /// propio alcance y su RLS, sellado por <c>AmbitoTenantExplicito</c>. El
    /// Tenant de origen no cuenta: Mi trabajo no lo enseña
    /// (<c>MiTrabajoVista</c>), así que llevar allí a quien solo tiene cartera
    /// en él sería llevarlo a una pantalla vacía. Lo caro (la visión de cartera
    /// entera) solo corre si hay algún Tenant además del de origen.
    /// </summary>
    private async Task<(bool CarteraEnOtroTenant, bool ActivoEsOrigen)> ResolverCarteraEnOtroTenantAsync(
        CancellationToken token)
    {
        var autorizados = await Mediator.Send(new ObtenerClientesAutorizadosQuery(), token);
        var origen = autorizados.Where(t => t.EsOrigen).Select(t => t.TenantId).ToHashSet();
        var activoEsOrigen = TenantActual.TenantId is { } activo && origen.Contains(activo);

        if (autorizados.All(t => t.EsOrigen))
            return (false, activoEsOrigen);

        var vision = await Mediator.Send(new ObtenerKpisGlobalesQuery(), token);
        var carteraEnOtro = vision.ClientesConMasRiesgo.Any(t =>
            !origen.Contains(t.TenantId) && t.TenantId != TenantActual.TenantId && !t.SinCarteraAsignada);

        return (carteraEnOtro, activoEsOrigen);
    }

    /// <summary>Base real de "N de M vigentes" (mockup Inicio TALVEG) — la misma suma que ya usa ObtenerKpisDashboardQuery para la tasa de cumplimiento, no una cifra nueva.</summary>
    private int TotalDocumentosConVigencia => _kpis is null ? 0
        : _kpis.DocumentosVencidos + _kpis.DocumentosUrgentes + _kpis.DocumentosProximos + _kpis.DocumentosVigentes;

    private int DocumentosFueraDeVigencia => _kpis is null ? 0
        : _kpis.DocumentosVencidos + _kpis.DocumentosUrgentes + _kpis.DocumentosProximos;

    /// <summary>
    /// Todo lo que el panel resume está a cero <b>y</b> nada espera en las colas:
    /// ni trabajadores, centros, documentos ni visitas, ni cola de atención, ni
    /// plataformas pendientes, ni reclamaciones sin respuesta. Solo entonces se
    /// cambia el panel por una explicación; si cualquiera de esas fuentes trae
    /// algo, el panel se pinta entero aunque el resto sea cero — decir «no hay
    /// datos» sobre un contexto que sí los tiene sería peor que los ceros.
    /// Es lo que ve, p. ej., un Operador CAE en su propio Tenant, donde todo el
    /// contenido vive en los Tenants propietarios que opera por delegación.
    /// </summary>
    private bool SinNingunDatoEnEsteContexto =>
        _kpis is { } k
        && k.TrabajadoresActivos == 0 && k.Centros == 0
        && k.DocumentosVigentes == 0 && k.DocumentosVencidos == 0
        && k.DocumentosUrgentes == 0 && k.DocumentosProximos == 0
        && k.VisitasProgramadas == 0 && k.VisitasUrgentes == 0
        && _queLlegoSinVer.Count == 0 && _pendientePorPlataforma.Count == 0
        && _sinRespuesta.Count == 0 && _proximamente.Count == 0
        && (_bandejaAgrupada is null
            || (_bandejaAgrupada.Grupos.Count == 0 && _bandejaAgrupada.SinGrupo.Count == 0));

    /// <summary>
    /// Centros distintos con al menos un item que de verdad les cierra el
    /// acceso, en la cola ya cargada — sin query nueva, es el mismo dato que ya
    /// pinta "Requiere atención". El criterio es
    /// <see cref="ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro"/>, el
    /// mismo que usa <see cref="TipoItemBandejaUi.BloqueaAccesoDeVerdad"/> para
    /// el badge «Bloquea acceso» de cada grupo: requisito que no es alta nueva
    /// (un alta sin completar no cierra nada; contarla decía «2 centros
    /// bloqueados» bajo un dashboard donde ningún grupo se declaraba
    /// bloqueante) o acreditación Rechazada que bloquea su Centro (D-7).
    /// </summary>
    private int CentrosBloqueados => _bandejaAgrupada is null ? 0
        : _bandejaAgrupada.Grupos.SelectMany(g => g.Items).Concat(_bandejaAgrupada.SinGrupo)
            .Where(i => ObtenerBandejaAgrupadaQueryHandler.BloqueaAccesoAlCentro(i) && i.CentroId is not null)
            .Select(i => i.CentroId!.Value)
            .Distinct()
            .Count();

    /// <summary>
    /// Aviso bajo el anillo cuando la consulta de KPI cuenta Centros de Trabajo
    /// bloqueados (<see cref="KpisDashboardDto.CentrosBloqueados"/>, P2.4): el
    /// porcentaje mide documentos vigentes y no puede leerse como acceso.
    /// </summary>
    private string TextoCentrosBloqueadosKpi => _kpis is not { CentrosBloqueados: > 0 } k ? string.Empty
        : k.CentrosBloqueados == 1
            ? "1 centro de trabajo con el acceso bloqueado: el porcentaje cuenta documentos, no acceso."
            : $"{k.CentrosBloqueados} centros de trabajo con el acceso bloqueado: el porcentaje cuenta documentos, no acceso.";

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

    // ------------------------------------------------ cabecera y subtítulos

    /// <summary>
    /// «Viernes, 16 de agosto» del mockup. La aplicación fija es-ES en
    /// Program.cs, así que el nombre del día sale ya en español; solo hay que
    /// subir la inicial, porque el formato de la cultura lo escribe en
    /// minúscula.
    /// </summary>
    private static string FechaDeHoy
    {
        get
        {
            var texto = DateTime.Now.ToString("dddd, d 'de' MMMM", CultureInfo.CurrentCulture);
            return texto.Length == 0 ? texto : char.ToUpper(texto[0], CultureInfo.CurrentCulture) + texto[1..];
        }
    }

    /// <summary>
    /// El mockup pone en la cabecera «Recuentos calculados en tiempo real». No
    /// se copia: esta pantalla no refresca sola, lee una vez al entrar, y
    /// prometer tiempo real haría que un dashboard de hace media hora pareciera
    /// de ahora. Se dice la hora de la lectura, que es lo que el propio mockup
    /// afirma unas secciones más abajo («Última actualización 09:12») y lo único
    /// que el código sostiene.
    /// </summary>
    private string? TextoActualizado =>
        _actualizadoA is { } instante ? $"Actualizado a las {instante:HH:mm}" : null;

    /// <summary>
    /// «3 nuevos desde ayer» del mockup, dicho sobre el corte real: el punto de
    /// corte lo fija la última actividad registrada del usuario
    /// (<see cref="ActividadUsuarioService"/>), que pudo ser hace una hora o
    /// hace una semana — «desde ayer» sería una afirmación que el dato no
    /// sostiene.
    /// </summary>
    private string ResumenQueLlegoSinVer
    {
        get
        {
            var cuantos = _queLlegoSinVer.Count;
            var cabeza = cuantos == 1 ? "1 nuevo" : $"{cuantos} nuevos";
            return _sinVerDesdeUtc is { } desde
                ? $"{cabeza} desde el {desde.ToLocalTime():dd/MM} a las {desde.ToLocalTime():HH:mm}"
                : cabeza;
        }
    }

    /// <summary>
    /// «5 grupos · 2 bloquean acceso» del mockup. Cuenta sobre los grupos que
    /// devuelve la consulta, no sobre los <see cref="MaximoGruposAtencion"/> que
    /// caben en pantalla: la consulta trae la cola entera, así que el número es
    /// el de verdad y el enlace «Ver todo en Mi trabajo» es la salida al resto.
    /// El «bloquean acceso» usa el mismo criterio que el badge de cada tarjeta
    /// (<see cref="TipoItemBandejaUi.BloqueaAccesoDeVerdad"/>) — si contara todo
    /// <c>BloqueaAcceso</c>, la cabecera diría que hay Centros cerrados mientras
    /// ninguna tarjeta de debajo lo dice.
    /// </summary>
    private string? ResumenAtencion
    {
        get
        {
            if (_bandejaAgrupada is not { } bandeja) return null;

            var grupos = bandeja.Grupos.Count;
            if (grupos == 0) return null;

            var bloquean = bandeja.Grupos.Count(TipoItemBandejaUi.BloqueaAccesoDeVerdad);
            var textoGrupos = grupos == 1 ? "1 grupo" : $"{grupos} grupos";

            return bloquean == 0
                ? textoGrupos
                : $"{textoGrupos} · {(bloquean == 1 ? "1 bloquea acceso" : $"{bloquean} bloquean acceso")}";
        }
    }
}

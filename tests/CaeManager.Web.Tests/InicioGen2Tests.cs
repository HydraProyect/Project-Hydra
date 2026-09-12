using System.Globalization;
using System.Reflection;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaAgrupada;
using CaeManager.Application.Bandeja.Queries.ObtenerBandejaGestor;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Reclamaciones.Queries.ObtenerReclamacionesSinRespuesta;
using CaeManager.Application.Tenants.Queries.ObtenerPerfilVocabularioActual;
using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Bandeja.Components;
using CaeManager.Web.Features.Dashboard.Pages;
using CaeManager.Web.Services;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using static CaeManager.Web.Tests.BandejaDatosDePrueba;

namespace CaeManager.Web.Tests;

/// <summary>
/// Inicio (<c>/</c>) contra el mockup <c>Inicio TALVEG.dc.html</c>.
///
/// <para>
/// Lo que el mockup añade y estos casos observan: la fecha de hoy y el
/// indicador de sistema en la cabecera, el recuento de «Qué llegó sin ver», la
/// línea «N grupos · M bloquean acceso» de «Requiere atención», la tarjeta de
/// cierre con salida al Calendario y la salida a «Mi trabajo» / «Visitas»
/// siempre visible. Y lo que el mockup NO pinta pero el código hace y no se
/// puede perder: que «bloquea acceso» solo se afirme cuando hay un bloqueo real
/// (un requisito de ALTA NUEVA no bloquea nada), que el enlace del anillo y los
/// KPI sigan donde estaban, y que una carga superada no pise el dashboard ya
/// pintado ni deje consultas trabajando para nadie al retirarse la pantalla.
/// </para>
/// </summary>
public class InicioGen2Tests : BunitContext
{
    private static readonly Guid Refrielectric = Guid.NewGuid();
    private static readonly Guid MontajesEbro = Guid.NewGuid();
    private static readonly Guid CentroNorte = Guid.NewGuid();
    private static readonly Guid CentroSur = Guid.NewGuid();

    public InicioGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    // ------------------------------------------------------------ cabecera

    [Fact]
    public void La_cabecera_lleva_el_antetitulo_el_saludo_y_la_fecha_de_hoy()
    {
        var cut = Renderizar(new MediadorDeInicio());

        cut.Find(".cabecera-pagina-kicker").TextContent.Trim().Should().Be("Dashboard");
        cut.Find("h1.titulo-pagina").TextContent.Trim().Should().StartWith("Buen");
        cut.Find(".inicio-fecha").TextContent.Trim().Should().Be(
            FechaEsperada(),
            "el mockup pone la fecha de hoy junto al saludo, con el día de la semana en español y la inicial en mayúscula");
    }

    /// <summary>
    /// El mockup promete «Recuentos calculados en tiempo real». Esta pantalla
    /// lee una vez al entrar y no vuelve a mirar, así que lo que se dice es la
    /// hora de esa lectura — la misma afirmación que el propio mockup hace unas
    /// secciones más abajo («Última actualización 09:12»).
    /// </summary>
    [Fact]
    public void El_indicador_de_sistema_dice_la_hora_de_la_lectura_y_no_promete_tiempo_real()
    {
        var cut = Renderizar(new MediadorDeInicio());

        var indicador = cut.Find(".inicio-indicador-sistema").TextContent.Trim();
        indicador.Should().MatchRegex(@"^Actualizado a las \d{2}:\d{2}$");
        cut.Markup.Should().NotContain("tiempo real",
            "prometer tiempo real haría que un dashboard de hace media hora pareciera de ahora");
    }

    // -------------------------------------------------- requiere atención

    /// <summary>
    /// «5 grupos · 2 bloquean acceso» del mockup, sobre los grupos que devuelve
    /// la consulta (la cola entera), no sobre los cinco que caben en pantalla.
    /// </summary>
    [Fact]
    public void La_cabecera_de_requiere_atencion_dice_cuantos_grupos_hay_y_cuantos_bloquean()
    {
        var cut = Renderizar(new MediadorDeInicio(
            Item("b1", TipoItemBandeja.RequisitoPendiente, Refrielectric, "Refrielectric S.A.") with { CentroId = CentroNorte },
            Item("v1", TipoItemBandeja.Vencido, MontajesEbro, "Montajes Ebro")));

        MetaDeSeccion(cut, "Requiere atención").Should().Be("2 grupos · 1 bloquea acceso");
    }

    /// <summary>
    /// Mismo defecto que cerró «Mi trabajo» (#599), un piso más arriba: un
    /// requisito de ALTA NUEVA no cierra ningún Centro, así que no puede
    /// contarse en «bloquean acceso» ni en la pista «N centros bloqueados» del
    /// KPI. El segundo grupo es el control positivo: con un requisito que NO es
    /// alta nueva, los dos recuentos sí cuentan.
    /// </summary>
    [Fact]
    public void Un_grupo_de_altas_nuevas_no_cuenta_como_bloqueo_ni_arriba_ni_en_el_kpi()
    {
        var cut = Renderizar(new MediadorDeInicio(
            Item("a1", TipoItemBandeja.RequisitoPendiente, Refrielectric, "Refrielectric S.A.")
                with
            { CentroId = CentroNorte, EsAltaNueva = true }));

        MetaDeSeccion(cut, "Requiere atención").Should().Be("1 grupo",
            "un alta sin completar no cierra ningún Centro: la cabecera no puede decir que bloquea");
        cut.Markup.Should().NotContain("Bloquea acceso");
        PistaDelKpi(cut, "Centros / plataformas").Should().BeNull(
            "la pista «N centros bloqueados» contaba también las altas nuevas y decía que había Centros cerrados donde no los había");
    }

    /// <summary>Control positivo del caso anterior: un requisito que NO es alta nueva sí cuenta en los dos sitios.</summary>
    [Fact]
    public void Un_requisito_que_no_es_alta_nueva_si_cuenta_como_bloqueo_arriba_y_en_el_kpi()
    {
        var cut = Renderizar(new MediadorDeInicio(
            Item("b1", TipoItemBandeja.RequisitoPendiente, Refrielectric, "Refrielectric S.A.")
                with
            { CentroId = CentroNorte },
            Item("b2", TipoItemBandeja.RequisitoPendiente, Refrielectric, "Refrielectric S.A.")
                with
            { CentroId = CentroSur }));

        MetaDeSeccion(cut, "Requiere atención").Should().Be("1 grupo · 1 bloquea acceso");
        cut.Markup.Should().Contain("Bloquea acceso");
        PistaDelKpi(cut, "Centros / plataformas").Should().Be("2 centros bloqueados");
    }

    /// <summary>
    /// La salida a la cola completa estaba solo cuando el resumen se quedaba
    /// corto (más de cinco grupos). El mockup la deja siempre: aquí se ven como
    /// mucho cinco grupos y quien quiere operar de verdad va a «Mi trabajo»
    /// tanto si son tres como si son treinta.
    /// </summary>
    [Fact]
    public void La_salida_a_mi_trabajo_esta_aunque_quepan_todos_los_grupos()
    {
        var cut = Renderizar(new MediadorDeInicio(
            Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.")));

        cut.FindAll("a").Should().ContainSingle(a =>
            a.GetAttribute("href") == "/bandeja" && a.TextContent.Contains("Ver todo en Mi trabajo"));
    }

    /// <summary>
    /// Cierre verificado (DDL-071) con la forma del mockup: tarjeta con marca de
    /// éxito, el próximo vencimiento conocido y salida al Calendario — no un
    /// párrafo suelto.
    /// </summary>
    [Fact]
    public void Sin_cola_el_cierre_es_una_tarjeta_que_dice_que_viene_despues_y_lleva_al_calendario()
    {
        var cut = Renderizar(new MediadorDeInicio
        {
            ProximoVencimiento = new ProximoVencimientoDto("A. García", "Formación PRL", new DateOnly(2026, 8, 28), 12)
        });

        var cierre = cut.Find(".inicio-cierre");
        cierre.QuerySelector(".inicio-cierre-texto")!.TextContent.Trim().Should().Be(
            "Nada pendiente ahora mismo. Próximo vencimiento: Formación PRL de A. García, el 28/08/2026 (en 12 días).");
        cierre.QuerySelector(".inicio-cierre-enlace")!.GetAttribute("href").Should().Be("/calendario");

        cut.FindAll("a").Should().NotContain(a => a.TextContent.Contains("Ver todo en Mi trabajo"),
            "sin cola no hay nada que ver en Mi trabajo");
    }

    // --------------------------------------------------- qué llegó sin ver

    /// <summary>
    /// El mockup dice «3 nuevos desde ayer». El corte real no es «ayer»: es la
    /// última actividad registrada del usuario, que pudo ser hace una hora o
    /// hace una semana. Se dice ese instante, que es lo que el dato sostiene.
    /// </summary>
    [Fact]
    public void El_recuento_de_lo_que_llego_sin_ver_dice_desde_cuando_cuenta()
    {
        var desde = new DateTime(2026, 8, 15, 16, 42, 0, DateTimeKind.Utc);
        var cut = Renderizar(
            new MediadorDeInicio(
                Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.") with { CreadaEnUtc = desde.AddHours(1) },
                Item("v2", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.") with { CreadaEnUtc = desde.AddHours(2) },
                Item("v3", TipoItemBandeja.Vencido, MontajesEbro, "Montajes Ebro") with { CreadaEnUtc = desde.AddHours(-5) }),
            actividad: new ActividadConAusencia(desde));

        var local = desde.ToLocalTime();
        MetaDeSeccion(cut, "Qué llegó sin ver").Should().Be(
            $"2 nuevos desde el {local:dd/MM} a las {local:HH:mm}",
            "el tercero es anterior al corte: llegó cuando el usuario todavía estaba mirando");
    }

    [Fact]
    public void Sin_ausencia_no_hay_seccion_de_lo_que_llego_sin_ver()
    {
        var cut = Renderizar(new MediadorDeInicio(
            Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.")));

        cut.Markup.Should().NotContain("Qué llegó sin ver");
    }

    // ------------------------------------------------------- próximamente

    /// <summary>
    /// El mockup sube «Ver todas las visitas» a la cabecera de la sección: la
    /// tabla muestra tres visitas como mucho, así que la salida al listado
    /// completo tiene que verse antes de recorrerla.
    /// </summary>
    [Fact]
    public void La_salida_a_visitas_esta_en_la_cabecera_de_proximamente_y_no_al_final()
    {
        var cut = Renderizar(new MediadorDeInicio
        {
            Visitas =
            [
                new VisitaListaDto(Guid.NewGuid(), CentroNorte, "Centro Norte", Refrielectric, "Refrielectric S.A.",
                    Guid.NewGuid(), "Montajes Ebro", new DateOnly(2026, 8, 18), new DateOnly(2026, 8, 20), 6,
                    DocumentacionCompleta: false, NotificadoCliente: false,
                    CaeManager.Domain.Visitas.OrigenVisita.Plataforma, CaeManager.Domain.Visitas.NivelUrgenciaVisita.Critica)
            ]
        });

        var cabecera = CabeceraDeSeccion(cut, "Próximamente");
        var enlace = cabecera.QuerySelector("a.inicio-seccion-enlace")!;
        enlace.GetAttribute("href").Should().Be("/visitas");
        enlace.TextContent.Should().Contain("Ver todas las visitas");

        cut.FindAll("a").Should().NotContain(a => a.TextContent.Contains("Ver todas en Visitas"),
            "el enlace de abajo se sustituye por el de la cabecera, no se duplica");
    }

    // ------------------------------------------------- lo que ya existía

    /// <summary>
    /// El mockup no pinta ni el desglose del anillo ni los cuatro KPI con su
    /// destino: los omite, no los retira. Este caso los ata para que un
    /// rediseño posterior no los pierda por descuido.
    /// </summary>
    [Fact]
    public void El_anillo_y_los_cuatro_kpi_conservan_sus_cifras_y_sus_destinos()
    {
        var cut = Renderizar(new MediadorDeInicio());

        cut.Find(".dashboard-resumen-anillo-detalle").TextContent.Trim()
            .Should().Be("3.812 de 4.383 documentos vigentes en cartera");

        DestinoDelKpi(cut, "Trabajadores activos").Should().Be("/trabajadores");
        DestinoDelKpi(cut, "Centros / plataformas").Should().Be("/centros");
        DestinoDelKpi(cut, "Vigentes").Should().Be("/documentos?estado=Vigente");
        DestinoDelKpi(cut, "Visitas programadas").Should().Be("/visitas");
    }

    [Fact]
    public void Un_grupo_sigue_pudiendo_expandirse_y_ensenar_sus_filas()
    {
        var cut = Renderizar(new MediadorDeInicio(
            Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.",
                trabajadorId: Guid.NewGuid(), trabajadorNombre: "Juan Pérez")));

        var grupo = cut.FindComponent<GrupoCola>();
        grupo.Markup.Should().NotContain("Juan Pérez", "el grupo llega plegado");

        cut.Find(".grupo-cola-cabecera").Click();

        cut.Find(".grupo-cola-contenido").TextContent.Should().Contain("Juan Pérez");
    }

    // -------------------------------------------------------------- carreras

    /// <summary>
    /// Dos cargas en vuelo: una recarga adelanta a la inicial. La respuesta de
    /// la superada llega después y NO puede pisar el dashboard ya pintado.
    ///
    /// <para>
    /// La recarga se dispara llamando a <c>CargarAsync</c>, que es lo que hace
    /// hoy el único disparador de la pantalla (el botón «Reintentar» del estado
    /// de error). No se dispara por el botón porque desde la UI de hoy no se
    /// puede: al pulsarlo, el estado de error se apaga y el botón desaparece,
    /// así que la segunda pulsación no existe. La guarda es por tanto seguro
    /// para cuando esta pantalla gane un segundo disparador —un refresco, una
    /// recarga tras enviar una reclamación como ya tiene «Mi trabajo»—, y el
    /// caso comprueba la guarda, no el camino de usuario.
    /// </para>
    ///
    /// <para>
    /// La recarga no se espera aquí: su manejador está esperando una respuesta
    /// retenida por el test, así que <c>await</c> colgaría el caso. Se guarda la
    /// tarea y se espera al final.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Una_carga_superada_no_pisa_el_dashboard_que_pinto_la_vigente()
    {
        var mediador = new MediadorDeInicio(Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A."));
        var inicial = new TaskCompletionSource<BandejaAgrupadaDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        var recarga = new TaskCompletionSource<BandejaAgrupadaDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retenidas[1] = inicial;
        mediador.Retenidas[2] = recarga;

        var cut = Renderizar(mediador);
        cut.FindAll(".esqueleto-lista").Should().NotBeEmpty("la carga inicial sigue retenida");

        var segunda = cut.InvokeAsync(() => Recargar(cut));
        cut.WaitForState(() => mediador.Bandejas == 2);

        recarga.SetResult(ObtenerBandejaAgrupadaQueryHandler.Agrupar(
            [Item("v9", TipoItemBandeja.Vencido, MontajesEbro, "Montajes Ebro")]));
        await segunda;
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Montajes Ebro"));

        inicial.SetResult(ObtenerBandejaAgrupadaQueryHandler.Agrupar(
            [Item("v1", TipoItemBandeja.Vencido, Refrielectric, "Refrielectric S.A.")]));

        // Un resultado vacío no es una ausencia: el pulso es una consulta
        // posterior a la bandeja dentro de la MISMA carga, así que su segundo
        // envío prueba que la respuesta tardía pasó de su await y se encontró
        // con la guarda de vigencia, en vez de dar por buena una ausencia sin
        // haber mirado.
        cut.WaitForState(() => mediador.Pulsos == 2);

        cut.Markup.Should().Contain("Montajes Ebro");
        cut.Markup.Should().NotContain("Refrielectric S.A.",
            "la respuesta de la carga superada contesta a una pregunta que ya nadie hizo");
    }

    /// <summary>
    /// Al retirar la pantalla, la consulta en vuelo tiene que quedarse sin
    /// token: si viajara con <c>CancellationToken.None</c>, seguiría trabajando
    /// para nadie y su respuesta llegaría a un componente ya desmontado.
    /// </summary>
    [Fact]
    public async Task Al_retirar_la_pantalla_se_cancela_la_carga_en_vuelo()
    {
        var mediador = new MediadorDeInicio();
        var inicial = new TaskCompletionSource<BandejaAgrupadaDto>(TaskCreationOptions.RunContinuationsAsynchronously);
        mediador.Retenidas[1] = inicial;

        var cut = Renderizar(mediador);
        cut.FindAll(".esqueleto-lista").Should().NotBeEmpty("la carga inicial sigue retenida");

        mediador.TokensDeCarga.Should().NotBeEmpty("las consultas de la carga inicial ya salieron");
        var token = mediador.TokensDeCarga[^1];
        token.CanBeCanceled.Should().BeTrue(
            "con CancellationToken.None, retirar la pantalla no cancelaría nada");
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue();

        Func<Task> llegaTarde = () => Renderer.Dispatcher.InvokeAsync(() =>
            inicial.SetResult(ObtenerBandejaAgrupadaQueryHandler.Agrupar([])));
        await llegaTarde.Should().NotThrowAsync("una respuesta tardía no puede tocar un componente ya retirado");
        Renderer.UnhandledException.IsCompleted.Should().BeFalse();
    }

    // ---------------------------------------------------------------- ayudas

    private IRenderedComponent<Inicio> Renderizar(MediadorDeInicio mediador, ActividadUsuarioService? actividad = null)
    {
        // La aplicación fija es-ES en Program.cs; aquí se fija en el flujo del
        // propio test, que es donde renderiza bUnit.
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<PuertaAccesoDatos>();
        // PanelResolverItem (las filas de «Qué llegó sin ver») anida un
        // TextoFechaCopiable, que inyecta ToastService.
        Services.AddScoped<ToastService>();
        Services.AddScoped<ContextWorkspaceService>();
        Services.AddScoped(_ => actividad ?? new ActividadSinAusencia());
        Services.AddScoped(_ => new UserManager<ApplicationUser>(
            new AlmacenSinUsuarios(), null!, null!, null!, null!, null!, null!, null!, null!));

        // Cualquier rol menos Cliente: «Requiere atención» reutiliza la cola del
        // Gestor CAE y el rol Cliente no la ve.
        AddAuthorization().SetAuthorized("marta").SetRoles(Roles.GestorCae);

        // La pantalla consulta RendererInfo.IsInteractive para no consumir la
        // ausencia durante el prerenderizado (ver ActividadUsuarioService). Sin
        // declararlo, bUnit lanza MissingRendererInfoException y la carga entera
        // cae en el estado de error — un fallo del arnés que se leería como un
        // fallo del producto.
        SetRendererInfo(new RendererInfo("Server", isInteractive: true));

        return Render<Inicio>();
    }

    /// <summary>
    /// Vuelve a cargar la pantalla por el mismo método que el botón
    /// «Reintentar». Es privado (no es API de nadie más), así que se llama por
    /// reflexión — mismo recurso que ya usan otros casos de este proyecto para
    /// observar el estado interno de una página (ver <c>LecturaIaGen2Tests</c>).
    /// </summary>
    private static Task Recargar(IRenderedComponent<Inicio> cut) =>
        (Task)typeof(Inicio)
            .GetMethod("CargarAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(cut.Instance, null)!;

    private static string FechaEsperada()
    {
        var texto = DateTime.Now.ToString("dddd, d 'de' MMMM", CultureInfo.GetCultureInfo("es-ES"));
        return char.ToUpper(texto[0], CultureInfo.GetCultureInfo("es-ES")) + texto[1..];
    }

    private static IElement CabeceraDeSeccion(IRenderedComponent<Inicio> cut, string titulo) =>
        cut.FindAll(".inicio-seccion-cabecera")
            .Single(c => c.QuerySelector("h2")!.TextContent.Trim() == titulo);

    private static string? MetaDeSeccion(IRenderedComponent<Inicio> cut, string titulo) =>
        CabeceraDeSeccion(cut, titulo).QuerySelector(".inicio-seccion-meta")?.TextContent.Trim();

    private static IElement TarjetaKpi(IRenderedComponent<Inicio> cut, string etiqueta) =>
        cut.FindAll(".rejilla-kpis-secundarios .tarjeta-metrica")
            .Single(t => t.QuerySelector(".tarjeta-metrica-etiqueta")!.TextContent.Trim() == etiqueta);

    private static string? PistaDelKpi(IRenderedComponent<Inicio> cut, string etiqueta) =>
        TarjetaKpi(cut, etiqueta).QuerySelector(".tarjeta-metrica-pista")?.TextContent.Trim();

    private static string? DestinoDelKpi(IRenderedComponent<Inicio> cut, string etiqueta) =>
        TarjetaKpi(cut, etiqueta).GetAttribute("href");

    // ------------------------------------------------------------- dobles

    /// <summary>Sin ausencia: «Qué llegó sin ver» no se pinta, que es el caso normal.</summary>
    private sealed class ActividadSinAusencia() : ActividadUsuarioService(null!, null!, null!)
    {
        public override Task<(bool Ausente, DateTime? DesdeParaResumen)> RegistrarYEvaluarAsync(
            bool interactivo, CancellationToken cancellationToken = default) =>
            Task.FromResult((false, (DateTime?)null));
    }

    /// <summary>El usuario vuelve tras una ausencia real, con el corte en <paramref name="desdeUtc"/>.</summary>
    private sealed class ActividadConAusencia(DateTime desdeUtc) : ActividadUsuarioService(null!, null!, null!)
    {
        public override Task<(bool Ausente, DateTime? DesdeParaResumen)> RegistrarYEvaluarAsync(
            bool interactivo, CancellationToken cancellationToken = default) =>
            Task.FromResult((true, (DateTime?)desdeUtc));
    }

    /// <summary>
    /// Doble de <see cref="IMediator"/> para Inicio: responde a las ocho
    /// consultas que hace la pantalla y revienta con cualquier otra. Es a
    /// propósito: si mañana la pantalla añade una novena, estos tests tienen que
    /// enterarse en vez de seguir en verde sobre un <c>default</c> silencioso.
    /// </summary>
    private sealed class MediadorDeInicio(params ItemBandejaDto[] items) : IMediator
    {
        /// <summary>Tres mil ochocientos doce vigentes de cuatro mil trescientos ochenta y tres — las cifras del mockup, para poder leerlas tal cual en los casos.</summary>
        public KpisDashboardDto Kpis { get; init; } = new(
            TrabajadoresActivos: 418, Centros: 37, DocumentosVencidos: 400, DocumentosUrgentes: 100,
            DocumentosProximos: 71, DocumentosVigentes: 3812, VisitasProgramadas: 12,
            TasaCumplimientoDocumental: 87);

        public IReadOnlyList<VisitaListaDto> Visitas { get; init; } = [];
        public IReadOnlyList<PendientePorPlataformaDto> Plataformas { get; init; } = [];
        public IReadOnlyList<ReclamacionSinRespuestaDto> SinRespuesta { get; init; } = [];
        public ProximoVencimientoDto? ProximoVencimiento { get; init; }
        public PerfilVocabularioTenant Perfil { get; init; } = PerfilVocabularioTenant.ClienteDirecto;
        public IReadOnlyList<ItemBandejaDto> Items { get; set; } = items;

        /// <summary>Token con el que viajó cada consulta, en orden de llegada — para comprobar que se cancelan al retirar la pantalla.</summary>
        public List<CancellationToken> TokensDeCarga { get; } = [];

        /// <summary>Retiene la consulta de la bandeja número N (1 = la de <c>OnInitializedAsync</c>) hasta que el test la resuelva.</summary>
        public Dictionary<int, TaskCompletionSource<BandejaAgrupadaDto>> Retenidas { get; } = [];

        public int Bandejas { get; private set; }

        /// <summary>
        /// Cuántas veces se pidió el pulso. Es una consulta POSTERIOR a la
        /// bandeja dentro de la misma carga, así que su contador dice cuántas
        /// cargas pasaron ya de ese await — la señal que necesita el caso de
        /// carreras para no dar por buena una ausencia sin haber mirado.
        /// </summary>
        public int Pulsos { get; private set; }

        public Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            TokensDeCarga.Add(cancellationToken);

            switch (request)
            {
                case ObtenerPerfilVocabularioActualQuery:
                    return Task.FromResult((TResponse)(object)Perfil);
                case ObtenerKpisDashboardQuery:
                    return Task.FromResult((TResponse)(object)Kpis);
                case ObtenerVisitasQuery:
                    return Task.FromResult((TResponse)(object)new ResultadoPaginado<VisitaListaDto>(
                        Visitas, Visitas.Count, 1, 3));
                case ObtenerPendientePorPlataformaQuery:
                    return Task.FromResult((TResponse)(object)Plataformas);
                case ObtenerBandejaAgrupadaQuery:
                    Bandejas++;
                    return Retenidas.TryGetValue(Bandejas, out var retenida)
                        ? Esperar<TResponse>(retenida.Task)
                        : Task.FromResult((TResponse)(object)ObtenerBandejaAgrupadaQueryHandler.Agrupar(Items));
                case ObtenerDesgloseDashboardQuery:
                    return Task.FromResult((TResponse)(object)new DesgloseDashboardDto([], [], [], ProximoVencimiento));
                case ObtenerPulsoEquipoQuery:
                    Pulsos++;
                    return Task.FromResult((TResponse)(object)new PulsoEquipoDto(0, 0, 0, null, false));
                case ObtenerReclamacionesSinRespuestaQuery:
                    return Task.FromResult((TResponse)(object)SinRespuesta);
                default:
                    throw new NotSupportedException($"Consulta no prevista en este test: {request.GetType().Name}.");
            }
        }

        private static async Task<TResponse> Esperar<TResponse>(Task<BandejaAgrupadaDto> pendiente) =>
            (TResponse)(object)await pendiente;

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            Task.CompletedTask;

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            Task.FromResult<object?>(null);

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    /// <summary>
    /// El saludo resuelve el nombre de pila por <c>UserManager</c> tras el
    /// primer render. Aquí no hay usuario que devolver: el saludo se queda en
    /// «Buenos días» a secas, que es exactamente lo que hace la pantalla cuando
    /// el usuario no tiene NombreCompleto.
    /// </summary>
    private sealed class AlmacenSinUsuarios : IUserStore<ApplicationUser>
    {
        public Task<IdentityResult> CreateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IdentityResult> DeleteAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Dispose() { }
        public Task<ApplicationUser?> FindByIdAsync(string userId, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
        public Task<ApplicationUser?> FindByNameAsync(string normalizedUserName, CancellationToken cancellationToken) => Task.FromResult<ApplicationUser?>(null);
        public Task<string?> GetNormalizedUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string> GetUserIdAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult(user.Id.ToString());
        public Task<string?> GetUserNameAsync(ApplicationUser user, CancellationToken cancellationToken) => Task.FromResult<string?>(user.UserName);
        public Task SetNormalizedUserNameAsync(ApplicationUser user, string? normalizedName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetUserNameAsync(ApplicationUser user, string? userName, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IdentityResult> UpdateAsync(ApplicationUser user, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

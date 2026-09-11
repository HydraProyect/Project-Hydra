using System.Globalization;
using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Common;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Application.Tenants.Queries.ObtenerClientesAutorizados;
using CaeManager.Infrastructure.Identity;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.Components.Forms;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using VisionCarteraPagina = CaeManager.Web.Features.VisionCartera.Pages.VisionCartera;

namespace CaeManager.Web.Tests;

/// <summary>
/// Visión de cartera contra su mockup Gen 2 («Vision Cartera TALVEG.dc.html»).
///
/// <para>
/// <b>El doble del mediador ejecuta el fan-out REAL.</b>
/// <see cref="ObtenerKpisGlobalesQuery"/> la resuelve
/// <see cref="ObtenerKpisGlobalesQueryHandler"/> de verdad: pide
/// <see cref="ObtenerClientesAutorizadosQuery"/> y, por cada organización, un
/// <see cref="ObtenerKpisDashboardQuery"/> dentro de su
/// <see cref="AmbitoTenantExplicito"/>. El doble contesta ese último LEYENDO el
/// ámbito, y aplica el alcance como <c>AlcanceDatosService</c> tras #571: con el
/// rol efectivo del usuario EN ESA organización —acceso total si el rol la
/// abarca; si no, lo que alcance su Asignación de Cartera allí, o nada y
/// <c>SinCarteraAsignada</c>—. Una petición por organización sin ámbito
/// explícito revienta, porque en producción se contaría con el tenant activo.
/// Así una pantalla que mezclase organizaciones —un rol para todas, las cifras
/// de una en la fila de otra, un 100% «sin cartera» contado como verde— no casa
/// con lo que se comprueba.
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la resolución real del rol por tenant
/// (<c>CurrentUserService</c>) ni de la cartera (<c>AlcanceDatosService</c>) —
/// eso lo prueba <c>FanOutMultiTenantFugaDeAlcanceTests</c> contra PostgreSQL—,
/// la autorización de la ruta (<c>AutorizacionDePaginasTests</c>), el endpoint
/// <c>/cuenta/cliente-activo</c> ni el aspecto (bUnit no evalúa CSS).
/// </para>
/// </summary>
public class VisionCarteraGen2Tests : BunitContext
{
    public VisionCarteraGen2Tests() => JSInterop.Mode = JSRuntimeMode.Loose;

    private static readonly Guid TenantPropio = Guid.Parse("0a0a0a0a-0000-0000-0000-000000000001");
    private static readonly Guid TenantA = Guid.Parse("a1a1a1a1-0000-0000-0000-000000000001");
    private static readonly Guid TenantB = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");

    private const string NombrePropio = "Consultora Arbeko S.L.";
    private const string NombreA = "Refrielectric S.A.";
    private const string NombreB = "Montajes Ebro S.L.";

    // ---------------------------------------------------------------- dobles

    /// <summary>Documentos por estado y actividad; la tasa se calcula como <c>ObtenerKpisDashboardQueryHandler</c>.</summary>
    private sealed record Datos(int Vencidos, int Urgentes, int Proximos, int Vigentes, int Trabajadores, int Centros)
    {
        public static readonly Datos Nada = new(0, 0, 0, 0, 0, 0);

        private int ConVigencia => Vigentes + Proximos + Urgentes + Vencidos;

        public KpisDashboardDto Kpis(bool sinCartera) => new(
            TrabajadoresActivos: Trabajadores, Centros: Centros,
            DocumentosVencidos: Vencidos, DocumentosUrgentes: Urgentes, DocumentosProximos: Proximos, DocumentosVigentes: Vigentes,
            VisitasProgramadas: 0,
            TasaCumplimientoDocumental: ConVigencia == 0 ? 100 : Vigentes * 100 / ConVigencia,
            SinCarteraAsignada: sinCartera);
    }

    /// <param name="Rol">Rol efectivo del usuario EN esta organización (el que resuelve CurrentUserService dentro del ámbito).</param>
    /// <param name="Completa">Lo que hay en la organización entera.</param>
    /// <param name="Cartera">Lo que alcanza su Asignación de Cartera aquí; null si no tiene ninguna.</param>
    private sealed record Organizacion(Guid TenantId, string Nombre, bool EsOrigen, string Rol, Datos Completa, Datos? Cartera)
    {
        /// <summary>Lo que ObtenerKpisDashboardQuery devolvería dentro del ámbito de esta organización.</summary>
        public KpisDashboardDto KpisConSuAlcance() =>
            Roles.AlcanzaTodaLaOrganizacion(Rol) ? Completa.Kpis(sinCartera: false)
            : Cartera is null ? Datos.Nada.Kpis(sinCartera: true)
            : Cartera.Kpis(sinCartera: false);
    }

    private sealed class Escenario
    {
        /// <summary>
        /// Por defecto: Administrador en su organización y Gestor CAE con
        /// Asignación de Cartera en las dos que os han delegado su gestión CAE.
        /// Las cifras de «Completa» en A y B son las que NO deben verse: son
        /// lo que contaría quien aplicase a todas el rol de la organización propia.
        /// </summary>
        public List<Organizacion> Organizaciones { get; } =
        [
            new(TenantPropio, NombrePropio, true, Roles.Administrador,
                Completa: new(0, 0, 2, 62, 74, 6), Cartera: null),
            new(TenantB, NombreB, false, Roles.GestorCae,
                Completa: new(90, 1, 1, 8, 400, 30), Cartera: new(9, 4, 6, 11, 61, 5)),
            new(TenantA, NombreA, false, Roles.GestorCae,
                Completa: new(40, 20, 30, 10, 300, 20), Cartera: new(12, 5, 9, 74, 84, 7)),
        ];

        public Organizacion this[Guid tenantId] => Organizaciones.Single(o => o.TenantId == tenantId);

        public void Cambiar(Guid tenantId, Func<Organizacion, Organizacion> cambio)
        {
            var i = Organizaciones.FindIndex(o => o.TenantId == tenantId);
            Organizaciones[i] = cambio(Organizaciones[i]);
        }

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Retener { get; set; } = _ => null;
    }

    private sealed class MediadorConFanOut(Escenario escenario) : IMediator
    {
        public List<(object Peticion, Guid? Ambito, CancellationToken Token)> Enviadas { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            var ambito = AmbitoTenantExplicito.TenantIdActual;
            Enviadas.Add((request, ambito, cancellationToken));

            if (escenario.Retener(request) is { } retenida)
                return (TResponse)(await retenida)!;

            object respuesta = request switch
            {
                ObtenerKpisGlobalesQuery q => await new ObtenerKpisGlobalesQueryHandler(this).Handle(q, cancellationToken),
                ObtenerClientesAutorizadosQuery => escenario.Organizaciones
                    .Select(o => new ClienteAutorizadoDto(o.TenantId, o.Nombre, o.EsOrigen)).ToList(),
                ObtenerKpisDashboardQuery => escenario[ambito
                    ?? throw new InvalidOperationException("KPIs de una organización pedidos sin ámbito explícito: se contarían con el tenant activo.")]
                    .KpisConSuAlcance(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {request.GetType().Name}.")
            };
            return (TResponse)respuesta;
        }

        public Task Send<TRequest>(TRequest request, CancellationToken cancellationToken = default) where TRequest : IRequest =>
            throw new NotSupportedException();

        public Task<object?> Send(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<TResponse> CreateStream<TResponse>(IStreamRequest<TResponse> request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public IAsyncEnumerable<object?> CreateStream(object request, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task Publish(object notification, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification => Task.CompletedTask;
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    private sealed class LoggerQueGuarda : ILogger<VisionCarteraPagina>
    {
        public List<string> Errores { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Error) Errores.Add(formatter(state, exception));
        }
    }

    // ---------------------------------------------------------------- arnés

    private sealed record Montaje(IRenderedComponent<VisionCarteraPagina> Cut, MediadorConFanOut Mediador, LoggerQueGuarda Logger);

    /// <summary>
    /// Sin <see cref="ICurrentUserService"/> registrado a propósito: la
    /// pantalla no debe leer ningún rol. Si vuelve a inyectarlo, el render
    /// falla aquí.
    /// </summary>
    private Montaje Renderizar(Escenario escenario)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

        var mediador = new MediadorConFanOut(escenario);
        var logger = new LoggerQueGuarda();
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddSingleton<ILogger<VisionCarteraPagina>>(logger);

        return new Montaje(Render<VisionCarteraPagina>(), mediador, logger);
    }

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    private static string TextoPagina(IRenderedComponent<VisionCarteraPagina> cut) =>
        string.Join(' ', cut.Find(".contenedor-pagina").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IReadOnlyList<IElement> Filas(IRenderedComponent<VisionCarteraPagina> cut) =>
        cut.FindAll(".tarjeta-organizaciones-riesgo tbody tr");

    private static IReadOnlyList<string> NombresEnTabla(IRenderedComponent<VisionCarteraPagina> cut) =>
        Filas(cut).Select(f => Texto(f.QuerySelector(".nombre-organizacion")!)).ToList();

    private static IReadOnlyList<string> LineasAmbito(IRenderedComponent<VisionCarteraPagina> cut) =>
        cut.FindAll(".ambito-cartera-linea").Select(Texto).ToList();

    private static (string Etiqueta, string Valor, string Pista) Metrica(IElement tarjeta) => (
        Texto(tarjeta.QuerySelector(".tarjeta-metrica-etiqueta")!),
        Texto(tarjeta.QuerySelector(".tarjeta-metrica-valor")!),
        tarjeta.QuerySelector(".tarjeta-metrica-pista") is { } pista ? Texto(pista) : string.Empty);

    private static (string Etiqueta, string Valor, string Pista) MetricaMedia(IRenderedComponent<VisionCarteraPagina> cut) =>
        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Select(Metrica).Single(m => m.Etiqueta == "Cumplimiento documental promedio");

    private static IReadOnlyList<(string Organizacion, string Vencidos, string Urgentes)> TablaDelGrafico(IRenderedComponent<VisionCarteraPagina> cut) =>
        cut.FindAll("figure.reparto-riesgo table.reparto-riesgo-datos tbody tr")
            .Select(f => (Texto(f.QuerySelector("th")!), Texto(f.QuerySelectorAll("td")[0]), Texto(f.QuerySelectorAll("td")[1])))
            .ToList();

    // ---------------------------------------------------------------- fan-out y alcance por organización

    [Fact]
    public void La_pantalla_pide_una_sola_consulta_global_y_cada_organizacion_se_cuenta_en_su_propio_ambito()
    {
        var montaje = Renderizar(new Escenario());

        var enviadas = montaje.Mediador.Enviadas;
        enviadas.Where(e => e.Peticion is ObtenerKpisGlobalesQuery).Should().ContainSingle()
            .Which.Ambito.Should().BeNull("la pantalla no fija ningún ámbito: el reparto lo hace la consulta");
        enviadas.Where(e => e.Peticion is ObtenerKpisDashboardQuery).Select(e => e.Ambito).Should().Equal(
            [TenantPropio, TenantB, TenantA],
            "una vuelta por organización autorizada, cada una con su ámbito, y ninguna pedida por la pantalla sin él");
        NombresEnTabla(montaje.Cut).Should().Equal(NombreA, NombreB, NombrePropio);
    }

    [Fact]
    public void Cada_organizacion_cuenta_con_el_rol_que_el_usuario_tiene_en_ella_y_no_con_el_de_la_suya()
    {
        // Administrador en su organización; en Refrielectric, Gestor CAE con
        // Asignación de Cartera; en Montajes Ebro, Gestor CAE sin ninguna.
        var escenario = new Escenario();
        escenario.Cambiar(TenantB, o => o with { Cartera = null });

        var cut = Renderizar(escenario).Cut;

        // Montajes Ebro empata a cero con la propia: el orden estable de la
        // consulta conserva el de las autorizadas, así que va la última.
        var filas = Filas(cut);
        NombresEnTabla(cut).Should().Equal(NombreA, NombrePropio, NombreB);
        filas[0].QuerySelectorAll("td").Skip(1).Take(3).Select(Texto).Should().Equal(
            ["12", "5", "74%"], "Refrielectric cuenta lo que alcanza su cartera ahí (12), no la organización entera (40)");
        filas[1].QuerySelectorAll("td").Skip(1).Take(3).Select(Texto).Should().Equal("0", "0", "96%");
        filas[2].QuerySelectorAll("td").Skip(1).Take(3).Select(Texto).Should().Equal(
            ["0", "0", "Sin Asignación de Cartera tuya"], "su 100% es «nada que evaluar», no «al día»");

        LineasAmbito(cut)[1].Should().Be("Cada organización cuenta solo lo que tu rol alcanza en ella; en 1 no tienes ninguna Asignación de Cartera");
        Texto(cut.Find(".aviso-sin-cartera")).Should().Be(
            $"En {NombreB} no tienes ninguna Asignación de Cartera: no cuentas ningún documento suyo y su tasa no entra en la media.");
        MetricaMedia(cut).Valor.Should().Be("82%", "(96×64 + 74×100) / 164: Montajes Ebro no pesa en la media");
        cut.Find(".pulso-en-verde").GetAttribute("aria-label").Should().Be(
            $"1 de 2 organizaciones con cartera con el cumplimiento documental en el 90% o más: {NombrePropio} 96%. "
            + $"No entra {NombreB}: sin Asignación de Cartera tuya.");
        cut.FindAll("svg.reparto-riesgo-grafico g.barra-organizacion")[2].TextContent.Should().Contain("sin cartera");
        TablaDelGrafico(cut)[2].Organizacion.Should().Be($"{NombreB}, sin Asignación de Cartera tuya");
        TextoPagina(cut).Should().NotContain("completa", "la pantalla no afirma un alcance que no puede saber por organización");
    }

    [Fact]
    public void Con_un_rol_que_abarca_cada_organizacion_cuenta_cada_una_entera_y_no_avisa_de_cartera()
    {
        var escenario = new Escenario();
        escenario.Cambiar(TenantA, o => o with { Rol = Roles.DireccionCae });
        escenario.Cambiar(TenantB, o => o with { Rol = Roles.Administrador });

        var cut = Renderizar(escenario).Cut;

        NombresEnTabla(cut).Should().Equal(NombreB, NombreA, NombrePropio);
        Filas(cut).Select(f => Texto(f.QuerySelectorAll("td")[1])).Should().Equal("90", "40", "0");
        LineasAmbito(cut)[1].Should().Be("Cada organización cuenta solo lo que tu rol alcanza en ella");
        cut.FindAll(".aviso-sin-cartera").Should().BeEmpty();
    }

    [Fact]
    public void Sin_Asignacion_de_Cartera_en_ninguna_no_pinta_un_100_por_ciento_en_verde()
    {
        var escenario = new Escenario();
        foreach (var id in new[] { TenantPropio, TenantA, TenantB })
            escenario.Cambiar(id, o => o with { Rol = Roles.CoordinadorCae, Cartera = null });

        var cut = Renderizar(escenario).Cut;

        MetricaMedia(cut).Should().Be(("Cumplimiento documental promedio", "—", "Sin Asignación de Cartera en ninguna organización"));
        Texto(cut.Find(".dashboard-resumen-anillo-titulo")).Should().Be("Sin cumplimiento que medir");
        cut.FindAll(".dashboard-resumen-tarjeta-anillo svg").Should().BeEmpty("un anillo al 100% afirmaría «al día»");
        Texto(cut.Find(".tarjeta-organizaciones-riesgo .texto-vacio-seccion"))
            .Should().Be("Ninguna organización tiene documentación vencida ni urgente en lo que tu rol alcanza.");
        cut.Find(".pulso-en-verde").TextContent.Should().StartWith("0");
    }

    // ---------------------------------------------------------------- cabecera y cifras

    [Fact]
    public void La_cabecera_nombra_organizaciones_y_cuenta_la_propia_y_las_delegadas()
    {
        var cut = Renderizar(new Escenario()).Cut;

        Texto(cut.Find("header.cabecera-pagina h1.titulo-pagina")).Should().Be("Todas las organizaciones que operas, de un vistazo");
        Texto(cut.Find("header.cabecera-pagina .cabecera-pagina-kicker")).Should().Be("Visión de cartera");
        LineasAmbito(cut).Should().Equal(
            ["3 organizaciones: la tuya y 2 que os han delegado su gestión CAE", "Cada organización cuenta solo lo que tu rol alcanza en ella"],
            "la propia sale de EsOrigen en ObtenerClientesAutorizadosQuery, no de la posición en la tabla");
    }

    [Fact]
    public void Las_cuatro_cifras_criticas_salen_de_la_consulta_con_su_contexto_real()
    {
        var cut = Renderizar(new Escenario()).Cut;

        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Select(Metrica).Should().Equal(
            ("Documentos vencidos", "21", "En 2 de 3 organizaciones"),
            ("Urgentes", "9", "Según el umbral urgente de cada organización"),
            ("Próximos a vencer", "17", "Según el umbral próximo de cada organización"),
            // (96×64 + 74×100 + 36×30) / 194 = 75; la media simple de 96, 74 y 36 sería 68.
            ("Cumplimiento documental promedio", "75%", "Ponderada por volumen de documentos"));

        // Los umbrales son ParametroSistema de cada organización, configurables:
        // una ventana fija escrita en la pantalla sería falsa en cuanto alguien los cambie.
        TextoPagina(cut).Should().NotContain("≤15").And.NotContain("≤30").And.NotContain("SLA").And.NotContain("Media simple");
    }

    [Fact]
    public void La_actividad_general_suma_las_organizaciones_sin_enlazar_a_listas_de_una_sola()
    {
        var cut = Renderizar(new Escenario()).Cut;

        var tarjetas = cut.FindAll(".rejilla-actividad-cartera .tarjeta-metrica");
        tarjetas.Select(Metrica).Select(m => (m.Etiqueta, m.Valor)).Should().Equal(
            ("Organizaciones", "3"), ("Trabajadores activos", "219"), ("Centros / plataformas", "18"));
        tarjetas.Should().OnlyContain(t => t.TagName == "DIV",
            "/trabajadores y /centros enseñan solo la organización activa: un enlace desde una suma de varias llevaría a otro número");
    }

    // ---------------------------------------------------------------- tabla

    [Fact]
    public void La_tabla_lista_todas_en_el_orden_de_la_consulta_y_marca_la_propia()
    {
        var cut = Renderizar(new Escenario()).Cut;

        var filas = Filas(cut);
        NombresEnTabla(cut).Should().Equal(NombreA, NombreB, NombrePropio);
        filas.Select(f => Texto(f.QuerySelector(".meta-organizacion")!)).Should().Equal(
            "Os ha delegado su gestión CAE", "Os ha delegado su gestión CAE", "Tu organización");
        filas.Select(f => f.QuerySelectorAll("td").Skip(1).Take(3).Select(Texto).ToList()).Should().BeEquivalentTo(
            new[] { new[] { "12", "5", "74%" }, new[] { "9", "4", "36%" }, new[] { "0", "0", "96%" } },
            o => o.WithStrictOrdering());
        filas[0].QuerySelectorAll(".badge")[0].GetAttribute("title").Should().Be($"{NombreA}: 12 documentos vencidos");
        filas[2].QuerySelectorAll(".badge")[0].ClassList.Should().Contain("badge-neutro", "un cero no es una señal de peligro");
    }

    [Fact]
    public void Cambiar_de_organizacion_es_un_POST_con_antiforgery_y_su_tenant()
    {
        var cut = Renderizar(new Escenario()).Cut;

        var formularios = cut.FindAll(".tarjeta-organizaciones-riesgo form");
        formularios.Should().HaveCount(3);
        var primero = formularios[0];
        primero.GetAttribute("method").Should().Be("post");
        primero.GetAttribute("action").Should().Be("/cuenta/cliente-activo");

        string? Campo(IElement form, string nombre) => form.QuerySelector($"input[name='{nombre}']")?.GetAttribute("value");
        Campo(primero, "tenantId").Should().Be(TenantA.ToString());
        Campo(primero, "returnUrl").Should().Be("/");
        Campo(primero, "__RequestVerificationToken").Should().Be("token-de-prueba");
        Campo(formularios[2], "tenantId").Should().Be(TenantPropio.ToString());

        var boton = primero.QuerySelector("button")!;
        boton.GetAttribute("type").Should().Be("submit");
        Texto(boton).Should().Be("Cambiar a esta organización →");
    }

    // ---------------------------------------------------------------- resumen, reparto y pulso

    [Fact]
    public void El_anillo_explica_que_la_media_pondera_y_con_que_tasas()
    {
        var cut = Renderizar(new Escenario()).Cut;

        Texto(cut.Find(".dashboard-resumen-anillo-titulo")).Should().Be("75% de cumplimiento documental promedio");
        Texto(cut.Find(".detalle-media-cartera")).Should().Be(
            "Media ponderada por el volumen de documentos con vencimiento de cada organización; tasas de las 3 que la forman: 96, 74 y 36%.");
    }

    [Fact]
    public void El_reparto_del_riesgo_dibuja_cada_organizacion_con_sus_cifras()
    {
        var cut = Renderizar(new Escenario()).Cut;

        var grupos = cut.FindAll("svg.reparto-riesgo-grafico g.barra-organizacion");
        grupos.Should().HaveCount(3);
        grupos[0].QuerySelector("rect.barra-vencidos title")!.TextContent.Should().Be($"{NombreA}: 12 documentos vencidos");
        grupos[0].QuerySelector("rect.barra-urgentes title")!.TextContent.Should().Be($"{NombreA}: 5 documentos urgentes, dentro de su umbral urgente");
        grupos[2].QuerySelectorAll("rect").Should().BeEmpty();
        grupos[2].TextContent.Should().Contain("sin riesgo");

        // La barra más alta es la del mayor total, y la escala es proporcional.
        int Alto(IElement g) => g.QuerySelectorAll("rect").Sum(r => int.Parse(r.GetAttribute("height")!, CultureInfo.InvariantCulture));
        Alto(grupos[0]).Should().BeGreaterThan(Alto(grupos[1]));
    }

    /// <summary>
    /// El SVG no es operable ni legible con lector de pantalla —sus &lt;title&gt;
    /// son ayuda de ratón—, así que va aria-hidden y sin foco, y su equivalente
    /// es una tabla con los nombres completos (las etiquetas del eje van
    /// recortadas) y las mismas cifras en el mismo orden, con nombre accesible.
    /// </summary>
    [Fact]
    public void El_reparto_del_riesgo_tiene_su_equivalente_accesible_en_una_tabla()
    {
        var cut = Renderizar(new Escenario()).Cut;

        var svg = cut.Find("figure.reparto-riesgo svg.reparto-riesgo-grafico");
        svg.GetAttribute("aria-hidden").Should().Be("true", "el lector de pantalla lee la tabla, no la imagen");
        svg.GetAttribute("focusable").Should().Be("false");
        svg.HasAttribute("role").Should().BeFalse();
        svg.HasAttribute("tabindex").Should().BeFalse("no tiene ninguna interacción a la que dar foco");

        var figura = cut.Find("figure.reparto-riesgo");
        var titulo = cut.Find($"#{figura.GetAttribute("aria-labelledby")}");
        Texto(titulo).Should().Be("Reparto del riesgo entre las 3 organizaciones");
        Texto(cut.Find("table.reparto-riesgo-datos caption")).Should().Be("Documentos vencidos y urgentes por organización");
        cut.FindAll("table.reparto-riesgo-datos thead th").Select(Texto).Should().Equal("Organización", "Vencidos", "Urgentes");

        TablaDelGrafico(cut).Should().Equal(
            (NombreA, "12", "5"), (NombreB, "9", "4"), (NombrePropio, "0", "0"));
        cut.FindAll("svg.reparto-riesgo-grafico .reparto-riesgo-etiqueta").Select(e => e.LastChild!.TextContent.Trim())
            .Should().Equal(["Refrielec…", "Montajes …", "Consultor…"], "el eje recorta; la tabla lleva el nombre entero");
    }

    [Fact]
    public void El_pulso_cuenta_con_vencidos_en_verde_y_en_riesgo_con_su_desglose()
    {
        var cut = Renderizar(new Escenario()).Cut;

        Texto(cut.Find(".pulso-cartera-frase")).Should().Be("2 de 3 organizaciones tienen documentación vencida.");

        var verde = cut.Find(".pulso-en-verde");
        verde.GetAttribute("aria-label").Should().Be(
            $"1 de 3 organizaciones con el cumplimiento documental en el 90% o más: {NombrePropio} 96%.");
        verde.TextContent.Should().StartWith("1");

        var riesgo = cut.Find(".pulso-en-riesgo");
        riesgo.GetAttribute("aria-label").Should().Be("30 documentos en riesgo: 21 vencidos más 9 urgentes.");
        riesgo.TextContent.Should().StartWith("30");
    }

    // ---------------------------------------------------------------- estados

    [Fact]
    public void Con_una_sola_organizacion_la_vista_se_declara_vacia()
    {
        var escenario = new Escenario();
        escenario.Organizaciones.RemoveAll(o => !o.EsOrigen);

        var cut = Renderizar(escenario).Cut;

        Texto(cut.Find(".estado-vacio h3")).Should().Be("Todavía no operas ninguna otra organización");
        cut.Find(".estado-vacio a").GetAttribute("href").Should().Be("/delegaciones");
        cut.FindAll(".tarjeta-metrica").Should().BeEmpty();
        cut.FindAll(".ambito-cartera").Should().BeEmpty();
    }

    [Fact]
    public async Task Si_la_carga_falla_se_puede_reintentar()
    {
        var escenario = new Escenario();
        var fallos = 1;
        escenario.Retener = p => p is ObtenerKpisGlobalesQuery && fallos-- > 0
            ? Task.FromException<object?>(new InvalidOperationException("caída de prueba"))
            : null;

        var (cut, mediador, logger) = Renderizar(escenario);

        Texto(cut.Find(".estado-vacio h3")).Should().Be("No pudimos cargar la visión de cartera");
        cut.FindAll(".tarjeta-metrica").Should().BeEmpty();
        logger.Errores.Should().ContainSingle();

        await cut.FindAll("button").Single(b => Texto(b) == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".estado-vacio").Should().BeEmpty();
        Filas(cut).Should().HaveCount(3);
        mediador.Enviadas.Count(e => e.Peticion is ObtenerKpisGlobalesQuery).Should().Be(2);
    }

    // ---------------------------------------------------------------- carreras y retirada

    /// <summary>
    /// Salir de la página cancela la consulta en curso, y su respuesta tardía
    /// ya no toca un componente retirado. Que el token quede cancelado
    /// demuestra además que el Dispose se ejecutó de verdad:
    /// DisposeComponentsAsync lo llama, cut.Dispose() de bUnit no.
    /// </summary>
    [Fact]
    public async Task Salir_de_la_pagina_cancela_la_carga_en_curso()
    {
        var respuesta = new TaskCompletionSource<object?>();
        var escenario = new Escenario { Retener = p => p is ObtenerKpisGlobalesQuery ? respuesta.Task : null };
        var (cut, mediador, _) = Renderizar(escenario);
        cut.FindAll(".esqueleto-lista").Should().ContainSingle("mientras la consulta no vuelve, se pinta la carga");

        var token = mediador.Enviadas.Single(e => e.Peticion is ObtenerKpisGlobalesQuery).Token;
        token.CanBeCanceled.Should().BeTrue("la consulta tiene que llevar el token del ciclo de la página");
        token.IsCancellationRequested.Should().BeFalse();

        await DisposeComponentsAsync();

        token.IsCancellationRequested.Should().BeTrue("salir de la página cancela la consulta en curso");
        var llegaTarde = () => cut.InvokeAsync(() => respuesta.SetResult(null));
        await llegaTarde.Should().NotThrowAsync("la respuesta tardía no toca un componente retirado");
    }

    /// <summary>
    /// Un fallo que vuelve después de salir —lo normal: la cancelación llega
    /// como excepción— no es un error de la pantalla: no se registra ni
    /// prepara un «No pudimos cargar» que nadie va a ver.
    /// </summary>
    [Fact]
    public async Task Un_fallo_que_llega_tras_salir_no_se_registra_como_error()
    {
        var respuesta = new TaskCompletionSource<object?>();
        var escenario = new Escenario { Retener = p => p is ObtenerKpisGlobalesQuery ? respuesta.Task : null };
        var (cut, _, logger) = Renderizar(escenario);

        await DisposeComponentsAsync();
        await cut.InvokeAsync(() => respuesta.SetException(new OperationCanceledException("cancelada al salir")));

        logger.Errores.Should().BeEmpty("la carga ya no es de nadie: su fallo no es un error que registrar");
    }

    /// <summary>
    /// El fan-out recorre las organizaciones de una en una: mientras la
    /// primera no responde, las demás ni se han pedido, y la pantalla no pinta
    /// un resultado parcial. Cuando responde, cada organización llega con lo
    /// suyo.
    /// </summary>
    [Fact]
    public async Task Mientras_una_organizacion_no_responde_no_se_pinta_un_resultado_parcial()
    {
        var respuestaPropia = new TaskCompletionSource<object?>();
        var escenario = new Escenario();
        escenario.Retener = p => p is ObtenerKpisDashboardQuery && AmbitoTenantExplicito.TenantIdActual == TenantPropio
            ? respuestaPropia.Task
            : null;
        var (cut, mediador, _) = Renderizar(escenario);

        cut.FindAll(".esqueleto-lista").Should().ContainSingle("la consulta global espera a todas sus organizaciones");
        mediador.Enviadas.Where(e => e.Peticion is ObtenerKpisDashboardQuery).Select(e => e.Ambito).Should().Equal([TenantPropio]);
        Filas(cut).Should().BeEmpty();

        await cut.InvokeAsync(() => respuestaPropia.SetResult(escenario[TenantPropio].KpisConSuAlcance()));

        cut.WaitForAssertion(() => NombresEnTabla(cut).Should().Equal(NombreA, NombreB, NombrePropio));
        Filas(cut).Select(f => Texto(f.QuerySelectorAll("td")[3])).Should().Equal("74%", "36%", "96%");
    }
}

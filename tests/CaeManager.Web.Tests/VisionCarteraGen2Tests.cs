using System.Globalization;
using System.Reflection;
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
using VisionCarteraPagina = CaeManager.Web.Features.VisionCartera.Pages.VisionCartera;

namespace CaeManager.Web.Tests;

/// <summary>
/// Visión de cartera contra su mockup Gen 2 («Vision Cartera TALVEG.dc.html»).
///
/// <para>
/// <b>Lo que esto SÍ observa:</b> qué se pinta con lo que devuelven
/// <see cref="ObtenerClientesAutorizadosQuery"/> y
/// <see cref="ObtenerKpisGlobalesQuery"/> —el doble agrega igual que el
/// handler real: suma, media simple truncada y orden por vencidos y urgentes,
/// sobre exactamente las organizaciones autorizadas—, cómo cambia el texto del
/// alcance según el rol efectivo que devuelve <see cref="ICurrentUserService"/>,
/// el formulario POST de cambio de organización, el reintento y qué pasa cuando
/// dos cargas vuelven fuera de orden (mediador controlado por
/// <see cref="TaskCompletionSource{TResult}"/>).
/// </para>
///
/// <para>
/// <b>Lo que NO observa:</b> la autorización de la ruta
/// (<c>AutorizacionDePaginasTests</c>), el acotado real por cartera dentro de
/// cada organización (<c>AlcanceDatosService</c>, Infrastructure), el endpoint
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

    private sealed class MediadorControlado(Func<object, Task<object?>> responder) : IMediator
    {
        public List<object> Enviados { get; } = [];

        public async Task<TResponse> Send<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default)
        {
            Enviados.Add(request);
            return (TResponse)(await responder(request))!;
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

    private sealed class UsuarioActualFalso(string? rol) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolActualAsync() => Task.FromResult(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(TenantPropio);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }

    private sealed class AntiforgeryFalso : AntiforgeryStateProvider
    {
        public override AntiforgeryRequestToken? GetAntiforgeryToken() => new("token-de-prueba", "__RequestVerificationToken");
    }

    /// <summary>Lo que <see cref="ObtenerKpisDashboardQuery"/> devolvería dentro de cada organización.</summary>
    private sealed record Datos(int Vencidos, int Urgentes, int Proximos, int Trabajadores, int Centros, int Tasa);

    /// <summary>
    /// Datos que ve el mediador. <see cref="ObtenerKpisGlobalesQuery"/> se
    /// responde <b>agregando como el handler real</b> sobre las organizaciones
    /// de <see cref="Autorizadas"/>: una pantalla que recalculase por su cuenta
    /// o reordenase no casaría con lo que se comprueba.
    /// </summary>
    private sealed class Escenario
    {
        public List<ClienteAutorizadoDto> Autorizadas { get; } =
        [
            new(TenantPropio, NombrePropio, true),
            new(TenantB, NombreB, false),
            new(TenantA, NombreA, false),
        ];

        public Dictionary<Guid, Datos> PorOrganizacion { get; } = new()
        {
            [TenantPropio] = new(0, 0, 2, 74, 6, 97),
            [TenantA] = new(12, 5, 9, 84, 7, 71),
            [TenantB] = new(9, 4, 6, 61, 5, 82),
        };

        /// <summary>Si devuelve una tarea, esa petición se resuelve cuando el test lo diga.</summary>
        public Func<object, Task<object?>?> Interceptar { get; set; } = _ => null;

        public Task<object?> Responder(object peticion) =>
            Interceptar(peticion) ?? Task.FromResult<object?>(peticion switch
            {
                ObtenerClientesAutorizadosQuery => Autorizadas.ToList(),
                ObtenerKpisGlobalesQuery => Kpis(),
                _ => throw new NotSupportedException($"Petición no prevista en este test: {peticion.GetType().Name}.")
            });

        public KpisGlobalesDto Kpis()
        {
            var porCliente = Autorizadas.Select(c => (Cliente: c, Datos: PorOrganizacion[c.TenantId])).ToList();
            return new KpisGlobalesDto(
                TotalClientes: porCliente.Count,
                DocumentosVencidos: porCliente.Sum(p => p.Datos.Vencidos),
                DocumentosUrgentes: porCliente.Sum(p => p.Datos.Urgentes),
                DocumentosProximos: porCliente.Sum(p => p.Datos.Proximos),
                TrabajadoresActivos: porCliente.Sum(p => p.Datos.Trabajadores),
                Centros: porCliente.Sum(p => p.Datos.Centros),
                TasaCumplimientoDocumentalPromedio: porCliente.Count == 0 ? 100 : (int)porCliente.Average(p => p.Datos.Tasa),
                ClientesConMasRiesgo: porCliente
                    .Select(p => new ClienteRiesgoDto(p.Cliente.TenantId, p.Cliente.Nombre, p.Datos.Vencidos, p.Datos.Urgentes, p.Datos.Tasa))
                    .OrderByDescending(c => c.DocumentosVencidos)
                    .ThenByDescending(c => c.DocumentosUrgentes)
                    .ToList());
        }

        /// <summary>
        /// Lo que produce una cartera que no alcanza nada: ObtenerKpisDashboardQuery
        /// devuelve ceros y tasa 100 en cada organización, y la consulta global
        /// descarta su <c>SinCarteraAsignada</c>.
        /// </summary>
        public Escenario SinCartera()
        {
            foreach (var id in PorOrganizacion.Keys.ToList())
                PorOrganizacion[id] = new(0, 0, 0, 0, 0, 100);
            return this;
        }
    }

    // ---------------------------------------------------------------- arnés

    private (IRenderedComponent<VisionCarteraPagina> Cut, MediadorControlado Mediador) Renderizar(Escenario escenario, string? rol = Roles.DireccionCae)
    {
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("es-ES");
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");

        var mediador = new MediadorControlado(escenario.Responder);
        Services.AddScoped<IMediator>(_ => mediador);
        Services.AddScoped<ICurrentUserService>(_ => new UsuarioActualFalso(rol));
        Services.AddScoped<AntiforgeryStateProvider, AntiforgeryFalso>();
        Services.AddScoped<PuertaAccesoDatos>();

        return (Render<VisionCarteraPagina>(), mediador);
    }

    private static string Texto(IElement elemento) => elemento.TextContent.Trim();

    private static string TextoPagina(IRenderedComponent<VisionCarteraPagina> cut) =>
        string.Join(' ', cut.Find(".contenedor-pagina").TextContent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static IReadOnlyList<IElement> Filas(IRenderedComponent<VisionCarteraPagina> cut) =>
        cut.FindAll(".tarjeta-organizaciones-riesgo tbody tr");

    private static IReadOnlyList<string> LineasAmbito(IRenderedComponent<VisionCarteraPagina> cut) =>
        cut.FindAll(".ambito-cartera-linea").Select(Texto).ToList();

    /// <summary>
    /// Lanza una segunda carga mientras la primera sigue en vuelo: lo que haría
    /// un doble clic en «Reintentar» antes de que el render retire el botón.
    /// Por reflexión porque, mientras carga, la pantalla no tiene ningún control
    /// que la dispare; si el método cambia de nombre, la prueba se entera.
    /// </summary>
    private static Task LanzarSegundaCarga(IRenderedComponent<VisionCarteraPagina> cut)
    {
        var cargar = typeof(VisionCarteraPagina).GetMethod("CargarAsync", BindingFlags.Instance | BindingFlags.NonPublic);
        cargar.Should().NotBeNull("si el método cambia de nombre, esta prueba tiene que enterarse, no pasar en falso");
        return cut.InvokeAsync(() => (Task)cargar!.Invoke(cut.Instance, null)!);
    }

    /// <summary>
    /// Espera a que la segunda carga termine y repinta, que es lo que hace un
    /// EventCallback al acabar su manejador. Invocada por reflexión, Blazor no
    /// sabe que ha terminado y no repintaría solo.
    /// </summary>
    private static async Task TerminarComoUnClic(IRenderedComponent<VisionCarteraPagina> cut, Task carga)
    {
        await carga;
        cut.Render();
    }

    private static (string Etiqueta, string Valor, string Pista) Metrica(IElement tarjeta) => (
        Texto(tarjeta.QuerySelector(".tarjeta-metrica-etiqueta")!),
        Texto(tarjeta.QuerySelector(".tarjeta-metrica-valor")!),
        tarjeta.QuerySelector(".tarjeta-metrica-pista") is { } pista ? Texto(pista) : string.Empty);

    // ---------------------------------------------------------------- cabecera y cifras

    [Fact]
    public void La_cabecera_nombra_organizaciones_y_cuenta_la_propia_y_las_delegadas()
    {
        var (cut, _) = Renderizar(new Escenario());

        Texto(cut.Find("header.cabecera-pagina h1.titulo-pagina")).Should().Be("Todas las organizaciones que operas, de un vistazo");
        Texto(cut.Find("header.cabecera-pagina .cabecera-pagina-kicker")).Should().Be("Visión de cartera");
        LineasAmbito(cut)[0].Should().Be("3 organizaciones: la tuya y 2 que os han delegado su gestión CAE",
            "la propia sale de EsOrigen en ObtenerClientesAutorizadosQuery, no de la posición en la tabla");
    }

    [Fact]
    public void Las_cuatro_cifras_criticas_salen_de_la_consulta_con_su_contexto_real()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.FindAll(".rejilla-kpis-criticos .tarjeta-metrica").Select(Metrica).Should().Equal(
            ("Documentos vencidos", "21", "En 2 de 3 organizaciones"),
            ("Urgentes", "9", "Según el umbral urgente de cada organización"),
            ("Próximos a vencer", "17", "Según el umbral próximo de cada organización"),
            ("Cumplimiento documental promedio", "83%", "Media simple de 3 organizaciones"));

        // Los umbrales son ParametroSistema de cada organización, configurables:
        // una ventana fija escrita en la pantalla sería falsa en cuanto alguien los cambie.
        TextoPagina(cut).Should().NotContain("≤15").And.NotContain("≤30").And.NotContain("SLA");
    }

    [Fact]
    public void La_actividad_general_suma_las_organizaciones_sin_enlazar_a_listas_de_una_sola()
    {
        var (cut, _) = Renderizar(new Escenario());

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
        var (cut, _) = Renderizar(new Escenario());

        var filas = Filas(cut);
        filas.Select(f => Texto(f.QuerySelector(".nombre-organizacion")!)).Should().Equal(NombreA, NombreB, NombrePropio);
        filas.Select(f => Texto(f.QuerySelector(".meta-organizacion")!)).Should().Equal(
            "Os ha delegado su gestión CAE", "Os ha delegado su gestión CAE", "Tu organización");
        filas.Select(f => f.QuerySelectorAll("td").Skip(1).Take(3).Select(Texto).ToList()).Should().BeEquivalentTo(
            new[] { new[] { "12", "5", "71%" }, new[] { "9", "4", "82%" }, new[] { "0", "0", "97%" } },
            o => o.WithStrictOrdering());
        filas[0].QuerySelectorAll(".badge")[0].GetAttribute("title").Should().Be($"{NombreA}: 12 documentos vencidos");
        filas[2].QuerySelectorAll(".badge")[0].ClassList.Should().Contain("badge-neutro", "un cero no es una señal de peligro");
    }

    [Fact]
    public void Cambiar_de_organizacion_es_un_POST_con_antiforgery_y_su_tenant()
    {
        var (cut, _) = Renderizar(new Escenario());

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

    // ---------------------------------------------------------------- resumen y pulso

    [Fact]
    public void El_anillo_explica_la_media_simple_con_las_tasas_que_la_forman()
    {
        var (cut, _) = Renderizar(new Escenario());

        Texto(cut.Find(".dashboard-resumen-anillo-titulo")).Should().Be("83% de cumplimiento documental promedio");
        Texto(cut.Find(".detalle-media-cartera")).Should().Be(
            "Media simple, sin ponderar por volumen, de las tasas de las 3 organizaciones: 97, 82 y 71%.");
    }

    [Fact]
    public void El_reparto_del_riesgo_dibuja_cada_organizacion_con_sus_cifras()
    {
        var (cut, _) = Renderizar(new Escenario());

        cut.Find("svg.reparto-riesgo-grafico").GetAttribute("aria-label").Should().Be(
            $"Documentos vencidos y urgentes por organización: {NombreA} 12 y 5, {NombreB} 9 y 4 y {NombrePropio} 0 y 0");
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

    [Fact]
    public void El_pulso_cuenta_con_vencidos_en_verde_y_en_riesgo_con_su_desglose()
    {
        var (cut, _) = Renderizar(new Escenario());

        Texto(cut.Find(".pulso-cartera-frase")).Should().Be("2 de 3 organizaciones tienen documentación vencida.");

        var verde = cut.Find(".pulso-en-verde");
        verde.GetAttribute("aria-label").Should().Be(
            $"1 de 3 organizaciones con el cumplimiento documental en el 90% o más: {NombrePropio} 97%.");
        verde.TextContent.Should().StartWith("1");

        var riesgo = cut.Find(".pulso-en-riesgo");
        riesgo.GetAttribute("aria-label").Should().Be("30 documentos en riesgo: 21 vencidos más 9 urgentes.");
        riesgo.TextContent.Should().StartWith("30");
    }

    // ---------------------------------------------------------------- rol y cartera

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    public void Un_rol_de_toda_la_organizacion_no_lee_ninguna_cartera(string rol)
    {
        var (cut, _) = Renderizar(new Escenario(), rol);

        LineasAmbito(cut)[1].Should().Be("Tu rol ve cada organización completa, sin acotar por cartera");
        cut.FindAll(".aviso-sin-cartera").Should().BeEmpty();
        TextoPagina(cut).Should().NotContain("tu cartera").And.NotContain("Gestores CAE que coordinas");
    }

    [Fact]
    public void Un_rol_de_toda_la_organizacion_sin_riesgo_lo_dice_sin_mencionar_cartera()
    {
        var (cut, _) = Renderizar(new Escenario().SinCartera(), Roles.DireccionCae);

        Texto(cut.Find(".tarjeta-organizaciones-riesgo .texto-vacio-seccion"))
            .Should().Be("Ninguna organización tiene documentación vencida ni urgente.");
        Filas(cut).Should().BeEmpty();
    }

    [Fact]
    public void Un_Gestor_CAE_con_cartera_ve_su_alcance_acotado_y_el_aviso()
    {
        // GestorCae no entra por el [Authorize] de la ruta, pero es rol
        // EFECTIVO de quien opera un workspace delegado con esa asignación.
        var (cut, _) = Renderizar(new Escenario(), Roles.GestorCae);

        LineasAmbito(cut)[1].Should().Be("Cada organización cuenta solo lo que alcanza tu cartera");
        Texto(cut.Find(".aviso-sin-cartera")).Should().Be(
            "Donde tu cartera no alcance nada, la organización sale con cero documentos y un 100% de cumplimiento: "
            + "esta vista todavía no distingue «sin cartera» de «todo al día».");
        Filas(cut).Should().HaveCount(3);
    }

    [Fact]
    public void Un_Gestor_CAE_sin_cartera_no_lee_que_todo_esta_al_dia_sin_matiz()
    {
        var (cut, _) = Renderizar(new Escenario().SinCartera(), Roles.GestorCae);

        Texto(cut.Find(".tarjeta-organizaciones-riesgo .texto-vacio-seccion"))
            .Should().Be("Ninguna organización tiene documentación vencida ni urgente dentro de tu cartera.");
        cut.FindAll(".aviso-sin-cartera").Should().ContainSingle(
            "los ceros y el 100% de una cartera vacía son indistinguibles de «al día» en lo que devuelve la consulta");
    }

    [Fact]
    public void Un_Coordinador_CAE_ve_la_cartera_de_los_Gestores_CAE_que_coordina()
    {
        var (cut, _) = Renderizar(new Escenario(), Roles.CoordinadorCae);

        LineasAmbito(cut)[1].Should().Be("Cada organización cuenta solo lo que alcanza la cartera de los Gestores CAE que coordinas");
        Texto(cut.Find(".aviso-sin-cartera")).Should().StartWith("Donde la cartera de los Gestores CAE que coordinas no alcance nada,");
    }

    // ---------------------------------------------------------------- estados

    [Fact]
    public void Con_una_sola_organizacion_la_vista_se_declara_vacia()
    {
        var escenario = new Escenario();
        escenario.Autorizadas.RemoveAll(c => !c.EsOrigen);

        var (cut, _) = Renderizar(escenario);

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
        escenario.Interceptar = p => p is ObtenerKpisGlobalesQuery && fallos-- > 0
            ? Task.FromException<object?>(new InvalidOperationException("caída de prueba"))
            : null;

        var (cut, mediador) = Renderizar(escenario);

        Texto(cut.Find(".estado-vacio h3")).Should().Be("No pudimos cargar la visión de cartera");
        cut.FindAll(".tarjeta-metrica").Should().BeEmpty();

        await cut.FindAll("button").Single(b => Texto(b) == "Reintentar").ClickAsync(new MouseEventArgs());

        cut.FindAll(".estado-vacio").Should().BeEmpty();
        Filas(cut).Should().HaveCount(3);
        mediador.Enviados.OfType<ObtenerKpisGlobalesQuery>().Should().HaveCount(2);
    }

    [Fact]
    public async Task Una_carga_que_vuelve_tarde_no_pisa_a_la_vigente()
    {
        var escenario = new Escenario();
        var pendientes = new Queue<TaskCompletionSource<object?>>();
        escenario.Interceptar = p =>
        {
            if (p is not ObtenerKpisGlobalesQuery) return null;
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendientes.Enqueue(tcs);
            return tcs.Task;
        };

        var (cut, _) = Renderizar(escenario);
        cut.FindAll(".esqueleto-lista").Should().ContainSingle("mientras la consulta no vuelve, se pinta la carga");

        // Segunda carga mientras la primera sigue en vuelo: lo que haría un
        // doble clic en «Reintentar» antes de que el render retire el botón.
        var segundaCarga = LanzarSegundaCarga(cut);
        cut.WaitForAssertion(() => pendientes.Should().HaveCount(2));

        var primera = pendientes.Dequeue();
        var segunda = pendientes.Dequeue();

        var vigente = new Escenario();
        vigente.Autorizadas.RemoveAt(1); // sin Montajes Ebro
        await cut.InvokeAsync(() => segunda.SetResult(vigente.Kpis()));
        await TerminarComoUnClic(cut, segundaCarga);
        Filas(cut).Should().HaveCount(2);

        // La primera vuelve después, con otro contenido: no debe pisar la vigente.
        await cut.InvokeAsync(() => primera.SetResult(new Escenario().Kpis()));
        cut.WaitForAssertion(() => Filas(cut).Select(f => Texto(f.QuerySelector(".nombre-organizacion")!))
            .Should().Equal(NombreA, NombrePropio));
        LineasAmbito(cut)[0].Should().Be("2 organizaciones: la tuya y 1 que os ha delegado su gestión CAE");
    }

    [Fact]
    public async Task Un_error_que_vuelve_tarde_no_tapa_los_datos_de_la_carga_vigente()
    {
        var escenario = new Escenario();
        var pendientes = new Queue<TaskCompletionSource<object?>>();
        escenario.Interceptar = p =>
        {
            if (p is not ObtenerKpisGlobalesQuery) return null;
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            pendientes.Enqueue(tcs);
            return tcs.Task;
        };

        var (cut, _) = Renderizar(escenario);
        var segundaCarga = LanzarSegundaCarga(cut);
        cut.WaitForAssertion(() => pendientes.Should().HaveCount(2));

        var primera = pendientes.Dequeue();
        var segunda = pendientes.Dequeue();
        await cut.InvokeAsync(() => segunda.SetResult(escenario.Kpis()));
        await TerminarComoUnClic(cut, segundaCarga);
        Filas(cut).Should().HaveCount(3);

        await cut.InvokeAsync(() => primera.SetException(new InvalidOperationException("respuesta vieja")));
        cut.WaitForAssertion(() => cut.FindAll(".estado-vacio").Should().BeEmpty());
        Filas(cut).Should().HaveCount(3);
    }
}

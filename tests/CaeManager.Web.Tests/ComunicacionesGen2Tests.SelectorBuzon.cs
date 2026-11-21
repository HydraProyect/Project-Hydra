using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Comunicaciones.Queries.ObtenerActividadBuzones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.Queries.ObtenerMensajesBuzonPersonal;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Integraciones.Queries.ObtenerConexionesIntegracion;
using CaeManager.Domain.Integraciones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Comunicaciones.Pages;

namespace CaeManager.Web.Tests;

/// <summary>
/// Desplegable «Buzón» de la bandeja (lienzo «Comunicaciones multi-buzón», L-9). La página pide los buzones con las lecturas que ya
/// existen (los del Tenant activo con ObtenerConexionesIntegracionQuery y su actividad; los de otra Empresa gestionada con
/// ObtenerBuzonesVistaUnificadaQuery, #2415) y manda el id de la conexión elegida a la query de la lista. Que el servidor lo sume con AND
/// a la visibilidad lo fijan los tests de Application y la integración bajo RLS; aquí se fija que el id viaja, que no viaja cuando no
/// toca y que el estado queda coherente con el selector «Empresa gestionada».
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private static readonly Guid BuzonNorte = Guid.Parse("b1b1b1b1-0000-0000-0000-000000000001");
    private static readonly Guid BuzonAvisos = Guid.Parse("b2b2b2b2-0000-0000-0000-000000000002");
    private static readonly Guid BuzonPersonalPropio = Guid.Parse("b3b3b3b3-0000-0000-0000-000000000003");
    private static readonly Guid BuzonEbro = Guid.Parse("b4b4b4b4-0000-0000-0000-000000000004");

    private static ConexionIntegracionListaDto Conexion(Guid id, string correo, string nombre, EstadoConexionIntegracion estado = EstadoConexionIntegracion.Habilitada,
        Guid? propietario = null) =>
        new(id, correo, nombre, null, null, estado, DateTime.UtcNow, propietario, null);

    /// <summary>
    /// Un Tenant activo con un buzón compartido con actividad, otro con error y el buzón personal del usuario; y una Empresa gestionada
    /// más con un buzón. Las Abiertas son distintas en cada sitio para que ningún recuento se confunda con otro.
    /// </summary>
    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) RenderizarConBuzones(
        Func<object, Task<object?>?>? ademas = null)
    {
        var escenario = new Escenario();
        escenario.ConexionesBuzon.Clear();
        escenario.ConexionesBuzon.AddRange([
            Conexion(BuzonNorte, "cae@norte.example.invalid", "CAE Norte"),
            Conexion(BuzonAvisos, "avisos@norte.example.invalid", "Avisos Norte", EstadoConexionIntegracion.ConError),
            Conexion(BuzonPersonalPropio, "yo@norte.example.invalid", "Mi correo", propietario: Guid.NewGuid())]);
        var conjunto = ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra);
        return RenderizarConEmpresas(escenario, peticion => ademas?.Invoke(peticion) ?? peticion switch
        {
            ObtenerTenantsBeneficiariosVistaUnificadaQuery => Task.FromResult<object?>(conjunto),
            ObtenerActividadBuzonesQuery => Task.FromResult<object?>(
                (IReadOnlyDictionary<Guid, ActividadBuzonDto>)new Dictionary<Guid, ActividadBuzonDto> { [BuzonNorte] = new(null, 4) }),
            ObtenerBuzonesVistaUnificadaQuery { TenantId: var t } when t == TenantOtro => Task.FromResult<object?>(new BuzonesDeTenantBeneficiarioDto(
                [Conexion(BuzonEbro, "cae@ebro.example.invalid", "CAE Ebro")],
                new Dictionary<Guid, string>(),
                new Dictionary<Guid, ActividadBuzonDto> { [BuzonEbro] = new(null, 7) })),
            ObtenerConversacionesUnificadasQuery => Task.FromResult<object?>(new PaginaConversacionesUnificadasDto(
                [FilaUnificada(TenantOtro, "Construcciones Ebro", "Renovación del TC2")], [], null)),
            _ => null
        });
    }

    private static void ElegirBuzon(IRenderedComponent<Bandeja> cut, string nombre)
    {
        cut.Find(".bz-boton").Click();
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".bz-grupo")));
        cut.FindAll(".bz-fila").Single(o => o.QuerySelector(".bz-n")!.TextContent == nombre).Click();
    }

    [Fact]
    public void El_desplegable_lista_todos_personal_y_los_buzones_por_Empresa_gestionada_con_abiertas_o_con_error()
    {
        var (cut, mediador) = RenderizarConBuzones();
        cut.WaitForElement(".eg-boton");
        Assert.Equal("Todos los buzones", cut.Find(".bz-nom").TextContent);
        // Carga perezosa: hasta abrir el desplegable no se pide ningún buzón de otra Empresa gestionada.
        Assert.DoesNotContain(mediador.Enviados, p => p is ObtenerBuzonesVistaUnificadaQuery);

        cut.Find(".bz-boton").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".bz-cab").Count));

        Assert.Equal("dialog", cut.Find(".bz-boton").GetAttribute("aria-haspopup"));
        Assert.Equal("Buzón · 5 opciones", cut.Find(".bz-pop-t").TextContent);
        var filas = cut.FindAll(".bz-fila").Select(f => f.QuerySelector(".bz-n")!.TextContent).ToList();
        Assert.Equal(["Todos los buzones", "Mi buzón personal", "CAE Norte", "Avisos Norte", "CAE Ebro"], filas);
        Assert.Equal("Talleres Norte2 buzones", cut.FindAll(".bz-cab")[0].TextContent.Trim());
        Assert.Equal("Construcciones Ebro1 buzón", cut.FindAll(".bz-cab")[1].TextContent.Trim());

        // «Todos los buzones» lleva el ámbito vigente y sus Abiertas (las del Tenant activo, 3); cada buzón, las suyas o «Con error».
        var todos = cut.FindAll(".bz-fila")[0];
        Assert.Equal("Talleres Norte", todos.QuerySelectorAll(".bz-d")[0].TextContent);
        Assert.Equal("3", todos.QuerySelector(".bz-c")!.TextContent);
        Assert.Equal("4", cut.FindAll(".bz-fila")[2].QuerySelector(".bz-c")!.TextContent);
        Assert.Equal("Con error", cut.FindAll(".bz-fila")[3].QuerySelector(".bz-c-error")!.TextContent);
        Assert.Equal("7", cut.FindAll(".bz-fila")[4].QuerySelector(".bz-c")!.TextContent);
        Assert.Equal("true", todos.GetAttribute("aria-selected"));

        // El buzón personal no sale como buzón compartido: tiene su propia unidad.
        Assert.DoesNotContain(cut.FindAll(".bz-fila"), f => f.TextContent.Contains("Mi correo", StringComparison.Ordinal));
        Assert.DoesNotContain("Tenant", cut.Find(".bz-selector").TextContent, StringComparison.Ordinal);

        // El listbox solo contiene opciones y grupos de opciones; los avisos quedan fuera.
        var listbox = cut.Find(".bz-panel [role=listbox]");
        Assert.All(listbox.Children, h => Assert.Contains(h.GetAttribute("role"), new[] { "option", "group" }));
        Assert.Null(listbox.QuerySelector("[role=status]"));
        Assert.Equal("Construcciones Ebro", cut.FindAll(".bz-grupo")[1].GetAttribute("aria-label"));
    }

    [Fact]
    public void La_busqueda_del_desplegable_estrecha_por_buzon_o_por_Empresa_gestionada_y_avisa_si_no_coincide_ninguno()
    {
        var (cut, _) = RenderizarConBuzones();
        cut.WaitForElement(".bz-boton").Click();
        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".bz-cab").Count));

        cut.Find(".bz-buscar").Input("ebro");
        Assert.Equal(["CAE Ebro"], cut.FindAll(".bz-grupo .bz-fila").Select(f => f.QuerySelector(".bz-n")!.TextContent).ToList());
        Assert.Single(cut.FindAll(".bz-cab"));

        cut.Find(".bz-buscar").Input("zzz");
        Assert.Empty(cut.FindAll(".bz-fila"));
        Assert.Contains(cut.FindAll(".bz-pop-t"), t => t.TextContent == "Ningún buzón coincide con «zzz»");
    }

    [Fact]
    public void Elegir_un_buzon_del_Tenant_activo_filtra_la_lista_y_sus_recuentos_por_esa_conexion_y_Todos_lo_quita()
    {
        var (cut, mediador) = RenderizarConBuzones();
        cut.WaitForElement(".eg-boton");
        var antes = mediador.Enviados.Count;
        Assert.All(mediador.Enviados.OfType<ObtenerConversacionesQuery>(), q => Assert.Null(q.ConexionIntegracionId));

        ElegirBuzon(cut, "CAE Norte");

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados.Skip(antes), p => p is ObtenerConversacionesQuery));
        Assert.All(mediador.Enviados.Skip(antes).OfType<ObtenerConversacionesQuery>(), q => Assert.Equal(BuzonNorte, q.ConexionIntegracionId));
        Assert.Equal("CAE Norte", cut.Find(".bz-nom").TextContent);
        Assert.Empty(cut.FindAll(".eu-lista"));

        var antesDeTodos = mediador.Enviados.Count;
        ElegirBuzon(cut, "Todos los buzones");

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados.Skip(antesDeTodos), p => p is ObtenerConversacionesQuery));
        Assert.All(mediador.Enviados.Skip(antesDeTodos).OfType<ObtenerConversacionesQuery>(), q => Assert.Null(q.ConexionIntegracionId));
        Assert.Equal("Todos los buzones", cut.Find(".bz-nom").TextContent);
    }

    [Fact]
    public void Elegir_un_buzon_de_otra_Empresa_gestionada_abre_su_vista_unificada_filtrada_y_el_selector_la_nombra()
    {
        var (cut, mediador) = RenderizarConBuzones();
        cut.WaitForElement(".eg-boton");

        ElegirBuzon(cut, "CAE Ebro");

        cut.WaitForElement(".eu-fila");
        var consulta = mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>().Last();
        Assert.Equal([TenantOtro], consulta.TenantIds!);
        Assert.Equal(BuzonEbro, consulta.ConexionIntegracionId);
        Assert.Equal("Construcciones Ebro", cut.Find(".eg-nom").TextContent);
        Assert.Equal("CAE Ebro", cut.Find(".bz-nom").TextContent);
        Assert.Empty(cut.FindAll("button[aria-label='Redactar correo']"));

        // Cambiar de Empresa gestionada en el selector no arrastra el buzón de la anterior.
        Elegir(cut, "Todas las Empresas gestionadas");

        cut.WaitForAssertion(() => Assert.Null(mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>().Last().TenantIds));
        Assert.Null(mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>().Last().ConexionIntegracionId);
        Assert.Equal("Todos los buzones", cut.Find(".bz-nom").TextContent);
    }

    [Fact]
    public void Desde_la_vista_unificada_un_buzon_del_Tenant_activo_vuelve_a_su_lista_filtrada()
    {
        var (cut, mediador) = RenderizarConBuzones();
        cut.WaitForElement(".eg-boton");
        ElegirBuzon(cut, "CAE Ebro");
        cut.WaitForElement(".eu-fila");
        var antes = mediador.Enviados.Count;

        ElegirBuzon(cut, "CAE Norte");

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".eu-lista")));
        Assert.Equal("Talleres Norte", cut.Find(".eg-nom").TextContent);
        Assert.Equal("CAE Norte", cut.Find(".bz-nom").TextContent);
        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados.Skip(antes), p => p is ObtenerConversacionesQuery));
        Assert.All(mediador.Enviados.Skip(antes).OfType<ObtenerConversacionesQuery>(), q => Assert.Equal(BuzonNorte, q.ConexionIntegracionId));
    }

    [Fact]
    public void Mi_buzon_personal_abre_la_vista_personal_y_el_boton_lo_nombra()
    {
        var (cut, mediador) = RenderizarConBuzones();
        cut.WaitForElement(".eg-boton");

        ElegirBuzon(cut, "Mi buzón personal");

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerMensajesBuzonPersonalQuery));
        Assert.Equal("Mi buzón personal", cut.Find(".bz-nom").TextContent);
    }

    [Fact]
    public void Una_Empresa_gestionada_cuyos_buzones_no_se_pueden_leer_no_sale_y_el_resto_si()
    {
        var (cut, _) = RenderizarConBuzones(p => p is ObtenerBuzonesVistaUnificadaQuery ? Task.FromResult<object?>(null) : null);
        cut.WaitForElement(".bz-boton").Click();

        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".bz-cab")));
        Assert.Equal("Talleres Norte2 buzones", cut.Find(".bz-cab").TextContent.Trim());
        Assert.DoesNotContain(cut.FindAll(".bz-fila"), f => f.TextContent.Contains("CAE Ebro", StringComparison.Ordinal));
    }

    // --- Hallazgos BAJA de la revisión de la #2418 ---

    /// <summary>BAJA 2: con «Todas» elegida, el monograma del botón es el icono «empresas», no unas iniciales.</summary>
    [Fact]
    public void Con_Todas_elegida_el_monograma_del_boton_es_el_icono_de_empresas()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        cut.WaitForElement(".eg-boton");
        Assert.Equal("TN", cut.Find(".eg-boton .eg-mono").TextContent);
        Assert.Null(cut.Find(".eg-boton .eg-mono").QuerySelector("svg"));

        Elegir(cut, "Todas las Empresas gestionadas");

        var mono = cut.Find(".eg-boton .eg-mono");
        Assert.Equal(string.Empty, mono.TextContent.Trim());
        var icono = Render<Icono>(p => p.Add(i => i.Nombre, "empresas").Add(i => i.Tamano, TamanoIcono.Pequeno));
        mono.QuerySelector("svg")!.OuterHtml.MarkupMatches(icono.Markup);
    }

    /// <summary>BAJA 5: el aviso de recuento parcial (role="status") queda fuera del listbox, que solo contiene opciones.</summary>
    [Fact]
    public void El_listbox_del_selector_de_Empresa_gestionada_solo_contiene_opciones_y_el_aviso_queda_fuera()
    {
        var conjunto = new TenantsBeneficiariosVistaUnificadaDto([EmpresaActiva, EmpresaOtra],
            [new TenantNoConsultadoDto(Guid.Parse("e5e5e5e5-0000-0000-0000-000000000005"), "Montajes Sur", false)]);
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(conjunto));
        cut.WaitForElement(".eg-boton").Click();

        Assert.Equal("dialog", cut.Find(".eg-boton").GetAttribute("aria-haspopup"));
        Assert.Equal("dialog", cut.Find(".eg-pop").GetAttribute("role"));
        var listbox = cut.Find(".eg-pop [role=listbox]");
        Assert.Equal(3, listbox.Children.Length);
        Assert.All(listbox.Children, h => Assert.Equal("option", h.GetAttribute("role")));
        Assert.Null(listbox.QuerySelector("[role=status]"));
        Assert.NotNull(cut.Find(".eg-pop").QuerySelector("[role=status].eg-pop-aviso"));
    }

    /// <summary>BAJA 3: con alguna Empresa gestionada sin leer, la lista vacía no dice «No quedan conversaciones».</summary>
    [Theory]
    [InlineData(true, "Lista incompleta")]
    [InlineData(false, "No quedan conversaciones")]
    public void La_lista_unificada_vacia_con_Empresas_sin_leer_dice_que_esta_incompleta(bool conSinLeer, string titulo)
    {
        var sinLeer = conSinLeer ? new[] { new TenantNoConsultadoDto(TenantOtro, "Construcciones Ebro", false) } : [];
        var (cut, _) = RenderizarConEmpresas(new Escenario(), peticion => peticion switch
        {
            ObtenerTenantsBeneficiariosVistaUnificadaQuery => Task.FromResult<object?>(ConjuntoDeEmpresas(
                EmpresaActiva with { Abiertas = 0 }, EmpresaOtra with { Abiertas = 0 })),
            ObtenerConversacionesUnificadasQuery => Task.FromResult<object?>(new PaginaConversacionesUnificadasDto([], sinLeer, null)),
            _ => null
        });
        cut.WaitForElement(".eg-boton");

        Elegir(cut, "Todas las Empresas gestionadas");

        cut.WaitForElement(".eu-vacio");
        Assert.Equal(titulo, cut.Find(".eu-vacio .estado-vacio h3").TextContent.Trim());
        Assert.Equal(conSinLeer, cut.FindAll(".eu-vacio button").Any(b => b.TextContent.Contains("Recargar la lista", StringComparison.Ordinal)));
        Assert.Equal(!conSinLeer, cut.FindAll(".eu-al-dia").Count == 1);
    }
}

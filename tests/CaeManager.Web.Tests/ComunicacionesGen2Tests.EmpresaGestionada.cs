using Bunit;
using CaeManager.Application.Bandeja.Queries.ObtenerMiTrabajoAgregado;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Comunicaciones.Eventos;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversaciones;
using CaeManager.Application.Comunicaciones.VistaUnificada;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Web.Features.Comunicaciones.Components;
using CaeManager.Web.Features.Comunicaciones.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Selector «Empresa gestionada» y vista unificada en solo consulta de la página de Comunicaciones (PR-S2b, L-1, D-6 b). La página
/// consume solo las dos lecturas de main (ObtenerTenantsBeneficiariosVistaUnificadaQuery y ObtenerConversacionesUnificadasQuery); la
/// elección viaja como filtro y nunca cambia el Tenant activo. Fija lo que se conserva de main (sin selector cuando no hay otra
/// Empresa gestionada, la lista de siempre al elegir el Tenant activo) y lo nuevo.
/// </summary>
public partial class ComunicacionesGen2Tests
{
    private static readonly Guid TenantActivo = Guid.Parse("c3c3c3c3-0000-0000-0000-000000000003");
    private static readonly Guid TenantOtro = Guid.Parse("d4d4d4d4-0000-0000-0000-000000000004");

    private sealed class TenantActualConId(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class NotificadorInerte : INotificadorMensajesTiempoReal
    {
        public IDisposable Suscribir(Guid tenantId, Func<MensajeWhatsAppRecibidoEvent, Task> callback) => new Baja();

        public Task PublicarAsync(MensajeWhatsAppRecibidoEvent aviso, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class Baja : IDisposable
        {
            public void Dispose() { }
        }
    }

    private static TenantsBeneficiariosVistaUnificadaDto ConjuntoDeEmpresas(params TenantBeneficiarioVistaUnificadaDto[] tenants) => new(tenants, []);

    private static readonly TenantBeneficiarioVistaUnificadaDto EmpresaActiva = new(TenantActivo, "Talleres Norte", true, 3);
    private static readonly TenantBeneficiarioVistaUnificadaDto EmpresaOtra = new(TenantOtro, "Construcciones Ebro", false, 2);

    private static ConversacionUnificadaDto FilaUnificada(Guid tenantId, string tenantNombre, string asunto) =>
        new(Guid.NewGuid(), tenantId, tenantNombre, 41, asunto, EstadoConversacion.Abierta, CanalConversacion.Correo,
            DateTime.UtcNow, null, null, null, "cae@ebro.example.invalid");

    /// <summary>La página con Tenant activo; las dos lecturas de la vista unificada las responde el test.</summary>
    private (IRenderedComponent<Bandeja> Cut, MediadorControlado Mediador) RenderizarConEmpresas(
        Escenario escenario, Func<object, Task<object?>?> vistaUnificada)
    {
        Services.AddSingleton<ILogger<ListaConversacionesUnificadas>>(_ => NullLogger<ListaConversacionesUnificadas>.Instance);
        escenario.Interceptar = vistaUnificada;
        return Renderizar(escenario, tenantActivo: TenantActivo);
    }

    private static Func<object, Task<object?>?> Responde(TenantsBeneficiariosVistaUnificadaDto conjunto, params ConversacionUnificadaDto[] filas) =>
        peticion => peticion switch
        {
            ObtenerTenantsBeneficiariosVistaUnificadaQuery => Task.FromResult<object?>(conjunto),
            ObtenerConversacionesUnificadasQuery q => Task.FromResult<object?>(new PaginaConversacionesUnificadasDto(
                filas.Where(f => q.TenantIds is null || q.TenantIds.Contains(f.TenantId)).ToList(), [], null)),
            _ => null
        };

    private static void Elegir(IRenderedComponent<Bandeja> cut, string texto)
    {
        cut.Find(".eg-boton").Click();
        cut.FindAll(".eg-opcion").Single(o => o.TextContent.Contains(texto, StringComparison.Ordinal)).Click();
    }

    [Fact]
    public void Sin_otra_Empresa_gestionada_no_hay_selector_y_la_pagina_es_la_de_main()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva)));

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerTenantsBeneficiariosVistaUnificadaQuery));
        Assert.Empty(cut.FindAll(".eg-boton"));
        Assert.DoesNotContain(mediador.Enviados, p => p is ObtenerConversacionesUnificadasQuery);
    }

    [Fact]
    public void Sin_Tenant_activo_no_se_pide_el_conjunto_de_Empresas_gestionadas()
    {
        var escenario = new Escenario { Interceptar = Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)) };
        Services.AddSingleton<ILogger<ListaConversacionesUnificadas>>(_ => NullLogger<ListaConversacionesUnificadas>.Instance);
        var (cut, mediador) = Renderizar(escenario);

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerConversacionesQuery));
        Assert.DoesNotContain(mediador.Enviados, p => p is ObtenerTenantsBeneficiariosVistaUnificadaQuery);
        Assert.Empty(cut.FindAll(".eg-boton"));
    }

    [Fact]
    public void El_selector_nombra_el_Tenant_activo_y_lista_las_Empresas_gestionadas_con_sus_abiertas()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaOtra, EmpresaActiva)));

        cut.WaitForElement(".eg-boton");
        Assert.Equal("Talleres Norte", cut.Find(".eg-nom").TextContent);
        Assert.Equal("TN", cut.Find(".eg-boton .eg-mono").TextContent);
        Assert.Equal("Empresa gestionada: Talleres Norte. Cambiar de Empresa gestionada", cut.Find(".eg-boton").GetAttribute("aria-label"));

        cut.Find(".eg-boton").Click();

        Assert.Equal("Empresas gestionadas · 2", cut.Find(".eg-pop-t").TextContent);
        var opciones = cut.FindAll(".eg-opcion");
        Assert.Equal(3, opciones.Count);
        Assert.Equal("Todas las Empresas gestionadas5", opciones[0].TextContent.Trim());
        Assert.Equal("CEConstrucciones Ebro2", opciones[1].TextContent.Trim());
        // La opción marcada es lo que muestra la lista: el Tenant activo.
        Assert.Equal("true", opciones[2].GetAttribute("aria-selected"));
        Assert.Equal("false", opciones[0].GetAttribute("aria-selected"));
        Assert.DoesNotContain("Tenant", cut.Find(".eg-selector").TextContent, StringComparison.Ordinal);
        // Con todas contadas, ni aviso ni total parcial.
        Assert.Empty(cut.FindAll(".eg-pop-aviso"));
        Assert.Null(opciones[0].QuerySelector(".eg-contador")!.GetAttribute("title"));
    }

    [Fact]
    public void La_busqueda_estrecha_las_opciones_y_avisa_cuando_ninguna_coincide()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        cut.WaitForElement(".eg-boton").Click();

        cut.Find(".eg-buscar").Input("ebro");
        Assert.Equal(2, cut.FindAll(".eg-opcion").Count);

        cut.Find(".eg-buscar").Input("zzz");
        Assert.Single(cut.FindAll(".eg-opcion"));
        Assert.Contains(cut.FindAll(".eg-pop-t"), t => t.TextContent == "Ninguna Empresa gestionada coincide con «zzz»");
    }

    [Fact]
    public void Elegir_otra_Empresa_gestionada_la_consulta_en_solo_consulta_sin_cambiar_de_Tenant_ni_ofrecer_acciones()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra),
            FilaUnificada(TenantOtro, "Construcciones Ebro", "Renovación del TC2")));
        cut.WaitForElement(".eg-boton");
        Assert.NotEmpty(cut.FindAll("button[aria-label='Redactar correo']"));

        Elegir(cut, "Construcciones Ebro");

        cut.WaitForElement(".eu-fila");
        var consulta = mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>().Single();
        Assert.Equal(new[] { TenantOtro }, consulta.TenantIds);
        Assert.Equal(GrupoEstadoConversacion.Abiertas, consulta.GrupoEstado);
        Assert.Equal("Construcciones Ebro · Abiertas", cut.Find(".eu-titulo").TextContent);
        Assert.Equal("Solo consulta. Para responder, abre arriba, en «Trabajando en», Construcciones Ebro.", cut.Find(".eu-aviso").TextContent);
        Assert.Equal("Construcciones Ebro", cut.Find(".eg-nom").TextContent);
        Assert.Contains("Renovación del TC2", cut.Find(".eu-fila").TextContent, StringComparison.Ordinal);
        // Solo consulta: sin «Redactar correo», sin filtros, sin buscador y sin filas que abran nada.
        Assert.Empty(cut.FindAll("button[aria-label='Redactar correo']"));
        Assert.Empty(cut.FindAll(".bandeja-filtros-form"));
        Assert.Empty(cut.FindAll(".bandeja-buscar-cabecera"));
        Assert.Empty(cut.FindAll(".eu-fila button, .eu-fila a"));
        Assert.DoesNotContain("Tenant", cut.Find(".eu-lista").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Elegir_Todas_pide_la_vista_unificada_sin_filtro_de_Empresa_gestionada()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra),
            FilaUnificada(TenantOtro, "Construcciones Ebro", "Renovación del TC2"),
            FilaUnificada(TenantActivo, "Talleres Norte", "Alta de trabajador")));
        cut.WaitForElement(".eg-boton");

        Elegir(cut, "Todas las Empresas gestionadas");

        cut.WaitForAssertion(() => Assert.Equal(2, cut.FindAll(".eu-fila").Count));
        Assert.Null(mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>().Single().TenantIds);
        Assert.Equal("Todas mis Empresas gestionadas · Abiertas", cut.Find(".eu-titulo").TextContent);
        Assert.Equal("Solo consulta. Para responder una conversación, abre su Empresa gestionada arriba, en «Trabajando en».",
            cut.Find(".eu-aviso").TextContent);
        // El botón nombra lo que se ve, «Todas», y no el Tenant activo; tampoco lleva sus iniciales.
        Assert.Equal("Todas las Empresas gestionadas", cut.Find(".eg-nom").TextContent);
        Assert.Equal("Empresa gestionada: Todas las Empresas gestionadas. Cambiar de Empresa gestionada",
            cut.Find(".eg-boton").GetAttribute("aria-label"));
        Assert.DoesNotContain("TN", cut.Find(".eg-boton .eg-mono").TextContent, StringComparison.Ordinal);
    }

    [Fact]
    public void Elegir_el_Tenant_activo_vuelve_a_la_lista_de_main()
    {
        var escenario = new Escenario();
        escenario.Conversaciones.Add(Conversacion("Documentación de marzo", ClienteRefrielectric));
        var (cut, mediador) = RenderizarConEmpresas(escenario, Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        cut.WaitForElement(".eg-boton");
        Elegir(cut, "Construcciones Ebro");
        cut.WaitForElement(".eu-lista");
        var listasDeMain = mediador.Enviados.Count(p => p is ObtenerConversacionesQuery);

        Elegir(cut, "Talleres Norte");

        Assert.Empty(cut.FindAll(".eu-lista"));
        Assert.Contains("Documentación de marzo", cut.Markup, StringComparison.Ordinal);
        Assert.NotEmpty(cut.FindAll("button[aria-label='Redactar correo']"));
        Assert.Single(mediador.Enviados.OfType<ObtenerConversacionesUnificadasQuery>());
        // Volver al Tenant activo no vuelve a pedir la lista: la de main sigue cargada y es la que se ve.
        Assert.Equal(listasDeMain, mediador.Enviados.Count(p => p is ObtenerConversacionesQuery));
    }

    [Fact]
    public void Con_Empresas_gestionadas_sin_leer_la_lista_lo_dice_y_no_pinta_Al_dia()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), peticion => peticion switch
        {
            // Recuentos a cero: así «Al día» solo lo puede quitar el aviso de no leídas, no el recuento del selector.
            ObtenerTenantsBeneficiariosVistaUnificadaQuery => Task.FromResult<object?>(ConjuntoDeEmpresas(
                EmpresaActiva with { Abiertas = 0 }, EmpresaOtra with { Abiertas = 0 })),
            ObtenerConversacionesUnificadasQuery => Task.FromResult<object?>(new PaginaConversacionesUnificadasDto(
                [], [new TenantNoConsultadoDto(TenantOtro, "Construcciones Ebro", false)], null)),
            _ => null
        });
        cut.WaitForElement(".eg-boton");

        Elegir(cut, "Todas las Empresas gestionadas");

        cut.WaitForElement(".eu-aviso-incompleto");
        Assert.Equal("No pudimos leer las conversaciones de Construcciones Ebro.", cut.Find(".eu-aviso-incompleto").TextContent);
        Assert.Empty(cut.FindAll(".eu-al-dia"));
    }

    [Fact]
    public void Si_el_servidor_rechaza_la_vista_unificada_la_pagina_queda_como_en_main()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), peticion => peticion is ObtenerTenantsBeneficiariosVistaUnificadaQuery
            ? Task.FromException<object?>(new UnauthorizedAccessException("fuera de la vista unificada"))
            : null);

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerTenantsBeneficiariosVistaUnificadaQuery));
        Assert.Empty(cut.FindAll(".eg-boton"));
        Assert.NotEmpty(cut.FindAll("button[aria-label='Redactar correo']"));
    }

    // ---------------------------------------------------------------- revisión r1 de PR-S2b

    /// <summary>
    /// MEDIA 1 (a): con una conversación abierta, elegir otra Empresa gestionada la cierra. No queda ni el detalle, ni las acciones, ni la
    /// nota fija del contacto, y «conversacion» sale de la URL. Detecta quitar el bloque que descarta la conversación abierta en
    /// <c>ElegirEmpresaGestionadaAsync</c> (la condición del panel de contexto es la segunda defensa y no basta para la URL ni el detalle).
    /// </summary>
    [Fact]
    public async Task Con_una_conversacion_abierta_elegir_otra_Empresa_gestionada_la_cierra_sin_dejar_detalle_ni_acciones()
    {
        var conversacion = Conversacion("Documentación de marzo", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion), NotaContacto = NotaDeCarmen() };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = RenderizarConEmpresas(escenario, Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.WaitForElement(".eg-boton");
        await SeleccionarFila(cut, "Documentación de marzo");
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindComponents<AccionCenter>()));
        Assert.NotEmpty(cut.FindComponents<NotaFijaContacto>());
        Assert.Contains("conversacion=", navegacion.Uri, StringComparison.Ordinal);

        Elegir(cut, "Construcciones Ebro");

        cut.WaitForElement(".eu-lista");
        Assert.Empty(cut.FindComponents<AccionCenter>());
        Assert.Empty(cut.FindComponents<NotaFijaContacto>());
        Assert.Empty(cut.FindAll(".bandeja-cliente"));
        Assert.Empty(cut.FindAll(".bandeja-centro-acciones"));
        Assert.Null(CampoPrivado<object?>(cut, "_detalle"));
        Assert.Null(CampoPrivado<Guid?>(cut, "_conversacionSeleccionadaId"));
        Assert.DoesNotContain("conversacion=", navegacion.Uri, StringComparison.Ordinal);
    }

    /// <summary>MEDIA 1 (b): un enlace «?conversacion=» abre una conversación del Tenant activo y saca de la vista unificada.</summary>
    [Fact]
    public async Task Un_enlace_a_una_conversacion_saca_de_la_vista_unificada()
    {
        var conversacion = Conversacion("Documentación de marzo", ClienteRefrielectric);
        var escenario = new Escenario { Detalle = _ => DetalleDe(conversacion) };
        escenario.Conversaciones.Add(conversacion);
        var (cut, _) = RenderizarConEmpresas(escenario, Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        cut.WaitForElement(".eg-boton");
        Elegir(cut, "Construcciones Ebro");
        cut.WaitForElement(".eu-lista");

        await cut.InvokeAsync(() => Services.GetRequiredService<NavigationManager>().NavigateTo($"comunicaciones?conversacion={conversacion.Id}"));

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".eu-lista")));
        Assert.Null(CampoPrivado<object?>(cut, "_eleccionEmpresa"));
        Assert.Equal(conversacion.Id, CampoPrivado<Guid?>(cut, "_conversacionSeleccionadaId"));
        Assert.Equal("Talleres Norte", cut.Find(".eg-nom").TextContent);
        cut.WaitForAssertion(() => Assert.NotEmpty(cut.FindAll(".bandeja-cliente")));
    }

    /// <summary>MEDIA 1 (c): «Clientes» y «Mi buzón personal» son del Tenant activo; pulsarlos saca de la vista unificada.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Cambiar_de_vista_saca_de_la_vista_unificada(int boton)
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        cut.WaitForElement(".eg-boton");
        Elegir(cut, "Construcciones Ebro");
        cut.WaitForElement(".eu-lista");

        cut.FindAll(".bandeja-nav-item")[boton].Click();

        cut.WaitForAssertion(() => Assert.Empty(cut.FindAll(".eu-lista")));
        Assert.Null(CampoPrivado<object?>(cut, "_eleccionEmpresa"));
        Assert.Equal("Talleres Norte", cut.Find(".eg-nom").TextContent);
    }

    /// <summary>BAJA 6: una vista que llega por la URL (Atrás del navegador) es del Tenant activo y saca de la vista unificada.</summary>
    [Fact]
    public async Task Atras_a_otra_vista_saca_de_la_vista_unificada()
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)));
        var navegacion = Services.GetRequiredService<NavigationManager>();
        cut.WaitForElement(".eg-boton");
        await cut.InvokeAsync(() => navegacion.NavigateTo("comunicaciones?vista=personal"));
        cut.WaitForAssertion(() => Assert.Equal("personal", CampoPrivado<string>(cut, "_vista")));
        Elegir(cut, "Construcciones Ebro");
        cut.WaitForElement(".eu-lista");

        // Atrás: el navegador vuelve a la URL anterior, la vista «Clientes».
        await cut.InvokeAsync(() => navegacion.NavigateTo("comunicaciones"));

        cut.WaitForAssertion(() => Assert.Equal("clientes", CampoPrivado<string>(cut, "_vista")));
        Assert.Null(CampoPrivado<object?>(cut, "_eleccionEmpresa"));
        Assert.Empty(cut.FindAll(".eu-lista"));
    }

    /// <summary>Con dos Empresas gestionadas que no incluyen el Tenant activo no hay selector: el conjunto no es el de esta sesión.</summary>
    [Fact]
    public void Sin_el_Tenant_activo_en_el_conjunto_no_hay_selector()
    {
        var (cut, mediador) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(
            EmpresaOtra, new TenantBeneficiarioVistaUnificadaDto(Guid.Parse("e5e5e5e5-0000-0000-0000-000000000005"), "Montajes Sur", false, 1))));

        cut.WaitForAssertion(() => Assert.Contains(mediador.Enviados, p => p is ObtenerTenantsBeneficiariosVistaUnificadaQuery));
        Assert.Empty(cut.FindAll(".eg-boton"));
        Assert.DoesNotContain(mediador.Enviados, p => p is ObtenerConversacionesUnificadasQuery);
    }

    /// <summary>
    /// MEDIA 2: las Empresas gestionadas que el servidor no pudo contar (NoConsultados) nunca son «cero». El total de «Todas» y el
    /// número de la cabecera se marcan como parciales y el desplegable avisa, sin «Tenant».
    /// </summary>
    [Fact]
    public void Con_Empresas_gestionadas_sin_contar_el_selector_marca_el_total_como_parcial_y_avisa()
    {
        var conjunto = new TenantsBeneficiariosVistaUnificadaDto([EmpresaActiva, EmpresaOtra],
            [new TenantNoConsultadoDto(Guid.Parse("e5e5e5e5-0000-0000-0000-000000000005"), "Montajes Sur", false)]);
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(conjunto));

        cut.WaitForElement(".eg-boton").Click();

        Assert.Equal("Empresas gestionadas · 2 de 3 contadas", cut.Find(".eg-pop-t").TextContent);
        Assert.Equal("No se han podido contar las conversaciones de Montajes Sur; el total es parcial.", cut.Find(".eg-pop-aviso").TextContent);
        var total = cut.FindAll(".eg-opcion")[0].QuerySelector(".eg-contador")!;
        Assert.Equal("5+", total.TextContent);
        Assert.Equal("Recuento parcial: faltan las Empresas gestionadas que no se han podido contar", total.GetAttribute("title"));
        Assert.DoesNotContain("Tenant", cut.Find(".eg-selector").TextContent, StringComparison.Ordinal);
    }

    /// <summary>
    /// La carrera de <c>_generacion</c>: la respuesta de una elección anterior que llega tarde no pisa la lista de la última elección.
    /// </summary>
    [Fact]
    public async Task Una_respuesta_tardia_de_otra_eleccion_no_pisa_la_lista_de_la_ultima()
    {
        var tardia = Retenida();
        var (cut, _) = RenderizarConEmpresas(new Escenario(), peticion => peticion switch
        {
            ObtenerTenantsBeneficiariosVistaUnificadaQuery => Task.FromResult<object?>(ConjuntoDeEmpresas(EmpresaActiva, EmpresaOtra)),
            ObtenerConversacionesUnificadasQuery { TenantIds: not null } => tardia.Task,
            ObtenerConversacionesUnificadasQuery => Task.FromResult<object?>(new PaginaConversacionesUnificadasDto(
                [FilaUnificada(TenantActivo, "Talleres Norte", "Alta de trabajador")], [], null)),
            _ => null
        });
        cut.WaitForElement(".eg-boton");
        Elegir(cut, "Construcciones Ebro");
        Elegir(cut, "Todas las Empresas gestionadas");
        cut.WaitForAssertion(() => Assert.Single(cut.FindAll(".eu-fila")));

        await cut.InvokeAsync(() => tardia.SetResult(new PaginaConversacionesUnificadasDto(
            [FilaUnificada(TenantOtro, "Construcciones Ebro", "Respuesta tardía")],
            [new TenantNoConsultadoDto(TenantActivo, "Talleres Norte", true)], null)));
        cut.Render();

        var fila = Assert.Single(cut.FindAll(".eu-fila"));
        Assert.Contains("Alta de trabajador", fila.TextContent, StringComparison.Ordinal);
        Assert.Empty(cut.FindAll(".eu-aviso-incompleto"));
        Assert.Equal("Todas mis Empresas gestionadas · Abiertas", cut.Find(".eu-titulo").TextContent);
    }

    /// <summary>«Tenant» no sale en pantalla, tampoco en los atributos que lee un lector de pantalla o que salen al pasar el ratón.</summary>
    [Fact]
    public void Tenant_no_sale_en_aria_label_ni_en_title_del_selector_ni_de_la_vista_unificada()
    {
        var conjunto = new TenantsBeneficiariosVistaUnificadaDto([EmpresaActiva, EmpresaOtra],
            [new TenantNoConsultadoDto(Guid.Parse("e5e5e5e5-0000-0000-0000-000000000005"), "Montajes Sur", false)]);
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(conjunto,
            FilaUnificada(TenantOtro, "Construcciones Ebro", "Renovación del TC2")));
        cut.WaitForElement(".eg-boton");
        Elegir(cut, "Todas las Empresas gestionadas");
        cut.WaitForElement(".eu-fila");
        cut.Find(".eg-boton").Click();

        var atributos = cut.FindAll(".eg-selector, .eg-selector *, .eu-lista, .eu-lista *")
            .SelectMany(e => new[] { e.GetAttribute("aria-label"), e.GetAttribute("title") })
            .Where(a => a is not null)
            .ToList();
        // Control positivo: el botón, el desplegable, el buscador, el total parcial y las filas llevan rótulo accesible o title.
        Assert.True(atributos.Count >= 6, $"solo {atributos.Count} atributos inspeccionados");
        Assert.All(atributos, a => Assert.DoesNotContain("Tenant", a, StringComparison.Ordinal));
    }

    /// <summary>
    /// BAJA 5: con la lista vacía y un recuento del selector mayor que cero para lo elegido, no se pinta «Al día»; con cero, sí. Los
    /// recuentos del Tenant activo y de la otra Empresa gestionada son distintos (revisión de la #2418, BAJA 1): con una Empresa
    /// gestionada elegida manda su recuento y no la suma; con «Todas», la suma y no el de una sola.
    /// </summary>
    [Theory]
    [InlineData("Construcciones Ebro", 3, 2, false)]
    [InlineData("Construcciones Ebro", 3, 0, true)]
    [InlineData("Todas las Empresas gestionadas", 3, 0, false)]
    [InlineData("Todas las Empresas gestionadas", 0, 2, false)]
    [InlineData("Todas las Empresas gestionadas", 0, 0, true)]
    public void Al_dia_no_se_pinta_si_el_selector_cuenta_abiertas_para_lo_elegido(string eleccion, int abiertasActiva, int abiertasOtra, bool alDia)
    {
        var (cut, _) = RenderizarConEmpresas(new Escenario(), Responde(ConjuntoDeEmpresas(
            EmpresaActiva with { Abiertas = abiertasActiva }, EmpresaOtra with { Abiertas = abiertasOtra })));
        cut.WaitForElement(".eg-boton");

        Elegir(cut, eleccion);

        cut.WaitForElement(".eu-vacio");
        Assert.Equal(alDia, cut.FindAll(".eu-al-dia").Count == 1);
    }

    [Theory]
    [InlineData("Construcciones Ebro", "CE")]
    [InlineData("  instalaciones   del sur", "ID")]
    [InlineData("Ñandú", "Ñ")]
    [InlineData("", "")]
    public void Las_iniciales_del_monograma_son_las_de_las_dos_primeras_palabras(string nombre, string esperadas) =>
        Assert.Equal(esperadas, SelectorEmpresaGestionada.Iniciales(nombre));
}

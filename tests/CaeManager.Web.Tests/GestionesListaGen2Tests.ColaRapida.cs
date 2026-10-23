using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Gestiones.Commands.CompletarGestion;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Components.Workspace;
using CaeManager.Web.Features.Gestiones;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Plataforma CAE visible en la Gestión y «Gestión rápida» (cola de pendientes del filtro actual). La regla de orden
/// de la cola se prueba aparte (<see cref="ColaDeGestionesTests"/>); aquí, lo que la pantalla hace con ella.
/// </summary>
public partial class GestionesListaGen2Tests
{
    private static readonly Guid TipoReconocimiento = Guid.NewGuid();
    private static readonly Guid TipoFormacion = Guid.NewGuid();

    /// <summary>
    /// <paramref name="dia"/> manda en el orden del listado (la más reciente primero): a mayor día, antes en la lista.
    /// </summary>
    private static GestionListaDto EnPlataforma(
        string trabajador, string? plataforma, Guid? tipo = null, string? url = "https://portal.example/entrada", int dia = 20,
        string cliente = "Obras del Ebro S.L.", string centro = "Centro Norte") => new(
        Guid.NewGuid(), Guid.NewGuid(), trabajador, Guid.NewGuid(), centro,
        tipo ?? TipoReconocimiento, tipo == TipoFormacion ? "Formación PRL" : "Reconocimiento médico", EstadoGestion.Pendiente,
        new DateTime(2026, 8, dia, 9, 0, 0, DateTimeKind.Utc),
        ClienteEmpresarialId: Guid.NewGuid(), ClienteEmpresarialNombre: cliente,
        PlataformaCaeNombre: plataforma, PlataformaCaeUrlPortal: plataforma is null ? null : url);

    private static IElement BotonGestionRapida(IRenderedComponent<Gestiones> cut) => cut.Find("button[data-gestion-rapida]");

    private static string TrabajadorEnElPanel(IRenderedComponent<Gestiones> cut) =>
        cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim();

    private static string ContadorDeLaCola(IRenderedComponent<Gestiones> cut) =>
        cut.Find("aside.vista-rapida-gestion .contador-cola-vista-rapida-gestion").TextContent.Trim();

    // ---- Plataforma CAE visible ----

    [Fact]
    public async Task La_vista_rapida_dice_en_que_plataforma_cae_se_hace_con_su_enlace_y_el_cliente_empresarial_y_el_centro()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { EnPlataforma("Nuria Salas Ortiz", "Nalanda", centro: "Centro Logístico Sur") } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        var bloque = cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]");
        bloque.QuerySelector(".plataforma-nombre-vista-rapida-gestion")!.TextContent.Trim().Should().Be("Nalanda");
        var enlace = bloque.QuerySelector("a")!;
        enlace.GetAttribute("href").Should().Be("https://portal.example/entrada");
        enlace.GetAttribute("target").Should().Be("_blank");
        enlace.GetAttribute("rel").Should().Contain("noopener").And.Contain("noreferrer");
        enlace.TextContent.Trim().Should().Be("Abrir portal ↗");
        bloque.TextContent.Should().Contain("Obras del Ebro S.L.").And.Contain("Centro Logístico Sur");
        bloque.TextContent.Should().NotContain("COD", "el código de cliente no viaja a este listado");
    }

    [Fact]
    public async Task Sin_plataforma_registrada_lo_dice_y_lleva_al_360_del_cliente_empresarial_sin_inventar_nada()
    {
        var gestion = EnPlataforma("Nuria Salas Ortiz", plataforma: null);
        var cut = Renderizar(new MediatorFalso { Almacen = { gestion } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        var bloque = cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]");
        bloque.TextContent.Should().Contain("Sin Plataforma CAE registrada");
        bloque.QuerySelector("a").Should().BeNull("sin plataforma no hay portal al que llevar");
        await BotonDeLaVistaRapida(cut, "Registrarla en el Cliente 360").ClickAsync(new MouseEventArgs());
        var frame = Services.GetRequiredService<ContextWorkspaceService>().FrameActual;
        frame!.Tipo.Should().Be(EntidadWorkspace.Cliente);
        frame.EntidadId.Should().Be(gestion.ClienteEmpresarialId);
    }

    [Fact]
    public async Task Una_direccion_que_no_es_http_ni_https_no_se_pinta_como_enlace()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { EnPlataforma("Nuria Salas Ortiz", "Nalanda", url: "javascript:alert(1)") } });

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        var bloque = cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]");
        bloque.QuerySelector(".plataforma-nombre-vista-rapida-gestion")!.TextContent.Trim().Should().Be("Nalanda");
        bloque.QuerySelector("a").Should().BeNull();
    }

    // ---- Gestión rápida ----

    [Fact]
    public void El_boton_Gestion_rapida_esta_en_la_cabecera_para_quien_escribe()
    {
        BotonGestionRapida(Renderizar(new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda") } }))
            .TextContent.Trim().Should().Be("Gestión rápida");
    }

    [Fact]
    public void Consulta_no_ve_el_boton_Gestion_rapida_porque_completar_exige_escritura()
    {
        this.ConRolDeEscritura(Roles.Consulta);

        var cut = Renderizar(new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda") } });

        cut.FindAll("button[data-gestion-rapida]").Should().BeEmpty();
    }

    [Fact]
    public async Task Gestion_rapida_abre_la_primera_pendiente_del_filtro_con_el_contador_N_de_M()
    {
        var mediador = new MediatorFalso
        {
            Almacen =
            {
                EnPlataforma("Ana Mena", "Nalanda", dia: 20), EnPlataforma("Bea Mena", "Dokify", TipoFormacion, dia: 19),
                EnPlataforma("Cris Vidal", "Nalanda", dia: 18), EnPlataforma("Hecha Vidal", "Nalanda", dia: 17) with { Estado = EstadoGestion.Completada }
            }
        };
        var cut = Renderizar(mediador, url: "gestiones?q=Mena");

        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());

        TrabajadorEnElPanel(cut).Should().Be("Ana Mena");
        ContadorDeLaCola(cut).Should().Be("1 de 2", "la cola solo recorre lo que el filtro «Mena» enseña, y solo las pendientes");
        var consulta = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Last();
        consulta.Estado.Should().Be(EstadoGestion.Pendiente);
        consulta.Busqueda.Should().Be("Mena", "la misma búsqueda que el listado, para respetar su alcance y su cartera");
        consulta.Pagina.Should().Be(1);
        consulta.TamanoPagina.Should().Be(ColaDeGestiones.TopeDeGestiones);
    }

    [Fact]
    public async Task Completar_en_la_cola_abre_la_siguiente_similar_primero_y_cuenta_N_de_M()
    {
        var ana = EnPlataforma("Ana", "Nalanda", dia: 20);
        var bea = EnPlataforma("Bea", "Dokify", TipoFormacion, dia: 19);
        var cris = EnPlataforma("Cris", "Nalanda", dia: 18);
        var mediador = new MediatorFalso { Almacen = { ana, bea, cris } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        TrabajadorEnElPanel(cut).Should().Be("Ana");

        await BotonDeLaVistaRapida(cut, "Completar y siguiente").ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().Equal([new CompletarGestionCommand(ana.Id, true)]);
        cut.WaitForAssertion(() => TrabajadorEnElPanel(cut).Should().Be("Cris", "mismo tipo y misma plataforma que Ana, aunque Bea va antes en el listado"));
        ContadorDeLaCola(cut).Should().Be("2 de 3");

        await BotonDeLaVistaRapida(cut, "Completar y siguiente").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => TrabajadorEnElPanel(cut).Should().Be("Bea"));
        ContadorDeLaCola(cut).Should().Be("3 de 3");
    }

    [Fact]
    public async Task Completar_la_ultima_cierra_la_cola_y_avisa_del_resumen()
    {
        var mediador = new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19) } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Saltar").ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Completar y siguiente").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty("no hay más que recorrer"));
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje == "Cola terminada. Completadas: 1; saltadas: 1.");
        BotonGestionRapida(cut).TextContent.Trim().Should().Be("Gestión rápida");
    }

    [Fact]
    public async Task Saltar_pasa_a_la_siguiente_sin_tocar_la_gestion()
    {
        var mediador = new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19) } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());

        await BotonDeLaVistaRapida(cut, "Saltar").ClickAsync(new MouseEventArgs());

        TrabajadorEnElPanel(cut).Should().Be("Bea");
        ContadorDeLaCola(cut).Should().Be("2 de 2");
        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().BeEmpty("saltar no completa nada");
    }

    [Fact]
    public async Task Salir_de_la_cola_cierra_el_panel_y_deja_las_gestiones_como_estaban()
    {
        var mediador = new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19) } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());

        await BotonDeLaVistaRapida(cut, "Salir de la cola").ClickAsync(new MouseEventArgs());

        cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty();
        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().BeEmpty();
        BotonGestionRapida(cut).TextContent.Trim().Should().Be("Gestión rápida", "sin cola en marcha, el botón empieza una nueva");
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        TrabajadorEnElPanel(cut).Should().Be("Ana");
        ContadorDeLaCola(cut).Should().Be("1 de 2");
    }

    [Fact]
    public async Task Cerrar_el_panel_pausa_la_cola_y_el_boton_ofrece_seguir_donde_iba()
    {
        var mediador = new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19) } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Saltar").ClickAsync(new MouseEventArgs());

        await cut.Find("aside.vista-rapida-gestion .boton-cerrar-vista-rapida-gestion").ClickAsync(new MouseEventArgs());

        BotonGestionRapida(cut).TextContent.Trim().Should().Be("Seguir con la cola (2 de 2)");
        var consultasAntes = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count();
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        TrabajadorEnElPanel(cut).Should().Be("Bea");
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count().Should().Be(consultasAntes, "seguir no vuelve a pedir la cola");
    }

    [Fact]
    public async Task Deshacer_tras_completar_en_la_cola_reabre_la_gestion_y_vuelve_a_ella()
    {
        var ana = EnPlataforma("Ana", "Nalanda", dia: 20);
        var cris = EnPlataforma("Cris", "Nalanda", dia: 18);
        var mediador = new MediatorFalso { Almacen = { ana, cris } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Completar y siguiente").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => TrabajadorEnElPanel(cut).Should().Be("Cris"));

        // El aviso de siempre (Flujos 10): ofrece Deshacer y manda la acción contraria sin encadenar otro aviso.
        var toasts = Services.GetRequiredService<ToastService>();
        var aviso = toasts.Mensajes.Last(m => m.Mensaje == "Gestión completada." && m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        mediador.Enviadas.OfType<CompletarGestionCommand>().Should().Equal(
            [new CompletarGestionCommand(ana.Id, true), new CompletarGestionCommand(ana.Id, false)]);
        toasts.Mensajes.Should().Contain(m => m.Mensaje == "Gestión reabierta." && m.TextoAccion == null);
        cut.WaitForAssertion(() => TrabajadorEnElPanel(cut).Should().Be("Ana", "la cola vuelve a la que se acababa de deshacer"));
        ContadorDeLaCola(cut).Should().Be("1 de 2");
        BotonDeLaVistaRapida(cut, "Completar y siguiente").Should().NotBeNull("vuelve a estar pendiente y a mano");
    }

    [Fact]
    public async Task Deshacer_la_ultima_tras_acabar_la_cola_tambien_la_devuelve()
    {
        var ana = EnPlataforma("Ana", "Nalanda");
        var mediador = new MediatorFalso { Almacen = { ana } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        await BotonDeLaVistaRapida(cut, "Completar y siguiente").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty());

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Last(m => m.Mensaje == "Gestión completada." && m.TextoAccion == "Deshacer");
        await cut.InvokeAsync(aviso.OnAccion!);

        cut.WaitForAssertion(() => TrabajadorEnElPanel(cut).Should().Be("Ana"));
        ContadorDeLaCola(cut).Should().Be("1 de 1");
    }

    [Fact]
    public async Task Sin_pendientes_en_el_filtro_Gestion_rapida_avisa_y_no_abre_nada()
    {
        var mediador = new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda") with { Estado = EstadoGestion.Completada } } };
        var cut = Renderizar(mediador, estado: nameof(EstadoGestion.Completada));
        var consultasAntes = mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count();

        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());

        cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty();
        Services.GetRequiredService<ToastService>().Mensajes.Should().Contain(m => m.Mensaje == "No hay gestiones pendientes en este filtro.");
        mediador.Enviadas.OfType<ObtenerGestionesQuery>().Count().Should().Be(consultasAntes, "un filtro de solo completadas no pide nada");
    }

    [Fact]
    public async Task Cambiar_el_filtro_olvida_la_cola_porque_ya_no_describe_lo_que_se_ve()
    {
        var mediador = new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19) } };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        await cut.Find("aside.vista-rapida-gestion .boton-cerrar-vista-rapida-gestion").ClickAsync(new MouseEventArgs());
        BotonGestionRapida(cut).TextContent.Trim().Should().StartWith("Seguir con la cola");

        Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().NavigateTo("gestiones?estado=Pendiente");
        cut.WaitForAssertion(() => BotonGestionRapida(cut).TextContent.Trim().Should().Be("Gestión rápida"));
    }

    [Fact]
    public async Task Filtrar_por_estado_en_la_franja_olvida_la_cola()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19) } });
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        await cut.Find("aside.vista-rapida-gestion .boton-cerrar-vista-rapida-gestion").ClickAsync(new MouseEventArgs());
        BotonGestionRapida(cut).TextContent.Trim().Should().StartWith("Seguir con la cola");

        await AlternarEstadoGestionFase1(cut, "Pendientes");

        cut.WaitForAssertion(() => BotonGestionRapida(cut).TextContent.Trim().Should().Be("Gestión rápida"));
    }

    [Fact]
    public async Task Una_cola_pedida_con_un_filtro_que_cambia_mientras_llega_no_se_abre()
    {
        var respuestaVieja = new TaskCompletionSource<object>();
        var mediador = new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19) } };
        mediador.Retener = p => p is ObtenerGestionesQuery { TamanoPagina: ColaDeGestiones.TopeDeGestiones } ? respuestaVieja.Task : null;
        var cut = Renderizar(mediador);

        var pulsacion = BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        await AlternarEstadoGestionFase1(cut, "Pendientes");
        respuestaVieja.SetResult(mediador.Filtrar(new ObtenerGestionesQuery(null, EstadoGestion.Pendiente, null)));
        await pulsacion;

        cut.FindAll("aside.vista-rapida-gestion").Should().BeEmpty("la cola que llegó era de un filtro que ya no es el que se ve");
        BotonGestionRapida(cut).TextContent.Trim().Should().Be("Gestión rápida");
    }

    [Fact]
    public async Task Completar_desde_su_fila_otra_gestion_de_la_cola_la_saca_de_ella_y_baja_el_total()
    {
        var mediador = new MediatorFalso
        {
            Almacen = { EnPlataforma("Ana", "Nalanda", dia: 20), EnPlataforma("Bea", "Nalanda", dia: 19), EnPlataforma("Cris", "Nalanda", dia: 18) }
        };
        var cut = Renderizar(mediador);
        await BotonGestionRapida(cut).ClickAsync(new MouseEventArgs());
        ContadorDeLaCola(cut).Should().Be("1 de 3");

        var filaBea = cut.FindAll("tbody tr").First(f => f.TextContent.Contains("Bea"));
        await filaBea.QuerySelector("button.gestion-completar")!.ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => ContadorDeLaCola(cut).Should().Be("1 de 2"));
        TrabajadorEnElPanel(cut).Should().Be("Ana", "la cola sigue donde iba");
        await BotonDeLaVistaRapida(cut, "Completar y siguiente").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => TrabajadorEnElPanel(cut).Should().Be("Cris", "Bea ya no está pendiente"));
    }

    [Fact]
    public async Task Fuera_de_la_cola_el_panel_sigue_igual_con_marcar_completada_y_eliminar()
    {
        var cut = Renderizar(new MediatorFalso { Almacen = { EnPlataforma("Ana", "Nalanda") } });

        await NombreEnLaFila(cut, "Ana").ClickAsync(new MouseEventArgs());

        cut.FindAll("aside.vista-rapida-gestion [data-cola-gestiones]").Should().BeEmpty();
        BotonDeLaVistaRapida(cut, "Marcar completada").Should().NotBeNull();
        BotonDeLaVistaRapida(cut, "Eliminar").Should().NotBeNull();
    }
}

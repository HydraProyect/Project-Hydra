using Bunit;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Gestiones;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Gestiones.Components;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.JSInterop;

namespace CaeManager.Web.Tests;

/// <summary>
/// Atajos de teclado del pie de la pantalla de pasos (especificación del 23/10, § 4.5; ATAJOS y onKey de
/// Gestion Pasos TALVEG.dc.html; decisión del 2026-11-08 «Constrúyelas antes»): → o «s» siguiente paso por hacer,
/// ← anterior, 1 Subido, 2 Validado, con sus indicadores en el pie. Lo que filtra el módulo JS (campo de texto,
/// modificadores, menú o diálogo) lo vigila <see cref="AtajosPasosGestionSincronizadoConJsTests"/>; aquí, lo que hace
/// la pantalla con cada tecla que le llega.
/// </summary>
public partial class GestionesAutomaticasPantallaTests
{
    private const string ModuloAtajos = "./js/atajos-pasos-gestion.js";

    private (IRenderedComponent<GestionAutomaticaDetalle> Cut, AtajosPasosGestion Atajos) DetallarConAtajos(
        MediatorFalso m, GestionAutomaticaDetalleDto detalle)
    {
        var modulo = JSInterop.SetupModule(ModuloAtajos);
        modulo.Mode = JSRuntimeMode.Loose;
        var cut = Detallar(m, detalle);
        cut.WaitForAssertion(() => modulo.Invocations["registrarAtajosPasosGestion"].Should().ContainSingle());
        var referencia = modulo.Invocations["registrarAtajosPasosGestion"].Single().Arguments[0]
            .Should().BeOfType<DotNetObjectReference<AtajosPasosGestion>>().Which;
        return (cut, referencia.Value);
    }

    private static string PasoAbiertoId(IRenderedComponent<GestionAutomaticaDetalle> cut) =>
        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto")!;

    [Fact]
    public void Atajos_el_pie_indica_las_cuatro_teclas_de_la_maqueta_en_su_orden()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        var atajos = cut.FindAll("[data-atajos-pie] [data-atajo]");
        atajos.Select(a => a.QuerySelector("kbd")!.TextContent).Should().Equal("→", "←", "1", "2");
        atajos.Select(a => a.TextContent).Should().Equal("→Siguiente", "←Anterior", "1Subido", "2Validado");
        cut.Find("[data-atajos-pie]").GetAttribute("aria-label").Should().Be("Atajos de teclado");
        cut.Find("[data-atajos-pie]").ParentElement!.ClassList.Should().Contain("gp-pie", "los indicadores van en el pie, como en la maqueta");
    }

    [Fact]
    public void Atajos_los_botones_anuncian_su_tecla_a_las_tecnologias_de_apoyo()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-accion='anterior']").GetAttribute("aria-keyshortcuts").Should().Be("ArrowLeft");
        cut.Find("[data-accion='primario']").GetAttribute("aria-keyshortcuts").Should().Be("ArrowRight");
        cut.Find("[data-opcion='subido']").GetAttribute("aria-keyshortcuts").Should().Be("1");
        cut.Find("[data-opcion='validado']").GetAttribute("aria-keyshortcuts").Should().Be("2");
        cut.Find("[data-opcion='rechazado']").HasAttribute("aria-keyshortcuts").Should().BeFalse();

        cut.Find("[data-opcion='subido']").Click();

        cut.Find("[data-accion='primario']").HasAttribute("aria-keyshortcuts").Should()
            .BeFalse("con una opción elegida el primario guarda, y → no guarda");
    }

    [Fact]
    public async Task Atajos_con_rol_Consulta_el_pie_no_indica_1_ni_2_y_las_cifras_no_eligen_nada()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar(Roles.Consulta);
        var (cut, atajos) = DetallarConAtajos(m, Detalle(pasos));

        cut.FindAll("[data-atajos-pie] [data-atajo]").Select(a => a.GetAttribute("data-atajo")).Should().Equal("siguiente", "anterior");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("1"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("2"));
        cut.FindAll("[data-campos-opcion]").Should().BeEmpty();
        cut.Find("[data-accion='primario']").TextContent.Should().Be("Siguiente");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("ArrowRight"));
        PasoAbiertoId(cut).Should().Be(pasos[1].AcreditacionId.ToString(), "Consulta sigue navegando por los pasos");
    }

    [Fact]
    public void Atajos_una_Gestion_sustituida_no_indica_1_ni_2()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos(), sustituida: true));

        cut.FindAll("[data-atajos-pie] [data-atajo]").Select(a => a.GetAttribute("data-atajo")).Should().Equal("siguiente", "anterior");
    }

    [Fact]
    public async Task Atajos_flecha_derecha_y_s_abren_el_siguiente_por_hacer_saltandose_los_hechos_y_flecha_izquierda_el_anterior()
    {
        var pasos = SeisPasosNuevos();
        pasos[1] = pasos[1] with { Estado = EstadoAcreditacion.Subida, Hecho = true };
        var m = Montar();
        var (cut, atajos) = DetallarConAtajos(m, Detalle(pasos));
        PasoAbiertoId(cut).Should().Be(pasos[0].AcreditacionId.ToString());

        await cut.InvokeAsync(() => atajos.RecibirAtajo("ArrowRight"));
        PasoAbiertoId(cut).Should().Be(pasos[2].AcreditacionId.ToString(), "→ se salta el paso ya subido");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("s"));
        PasoAbiertoId(cut).Should().Be(pasos[3].AcreditacionId.ToString());

        await cut.InvokeAsync(() => atajos.RecibirAtajo("ArrowLeft"));
        PasoAbiertoId(cut).Should().Be(pasos[2].AcreditacionId.ToString());
        await cut.InvokeAsync(() => atajos.RecibirAtajo("ArrowLeft"));
        PasoAbiertoId(cut).Should().Be(pasos[1].AcreditacionId.ToString(), "← es «Anterior»: recorre también los hechos, como el botón");

        m.Enviados.Should().BeEmpty("navegar con el teclado no marca nada");
    }

    [Fact]
    public async Task Atajos_flecha_izquierda_en_el_primer_paso_no_hace_nada()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var (cut, atajos) = DetallarConAtajos(m, Detalle(pasos));

        await cut.InvokeAsync(() => atajos.RecibirAtajo("ArrowLeft"));

        PasoAbiertoId(cut).Should().Be(pasos[0].AcreditacionId.ToString());
    }

    [Fact]
    public async Task Atajos_flecha_derecha_no_guarda_la_opcion_elegida_y_la_descarta_como_al_abrir_otro_paso()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var (cut, atajos) = DetallarConAtajos(m, Detalle(pasos));

        cut.Find("[data-opcion='subido']").Click();
        await cut.InvokeAsync(() => atajos.RecibirAtajo("ArrowRight"));

        PasoAbiertoId(cut).Should().Be(pasos[1].AcreditacionId.ToString());
        cut.Find("[data-opcion='subido']").GetAttribute("aria-checked").Should().Be("false");
        m.Enviados.Should().BeEmpty();
    }

    [Fact]
    public async Task Atajos_flecha_derecha_en_el_ultimo_paso_por_hacer_abre_la_Siguiente_Gestion()
    {
        var pasos = SeisPasosNuevos().Select(p => p with { Estado = EstadoAcreditacion.Aceptada, Hecho = true }).ToList();
        pasos[5] = pasos[5] with { Estado = EstadoAcreditacion.PendienteDeSubir, Hecho = false };
        var m = Montar();
        var otra = Guid.NewGuid();
        m.Listado = new ListadoGestionesAutomaticasDto([Fila(GestionId, EstadoGestionAutomatica.EnCurso), Fila(otra, EstadoGestionAutomatica.EnCurso)], Recuentos());
        var (cut, atajos) = DetallarConAtajos(m, Detalle(pasos));

        await cut.InvokeAsync(() => atajos.RecibirAtajo("ArrowRight"));

        cut.WaitForAssertion(() => Services.GetRequiredService<NavigationManager>().Uri.Should().EndWith($"gestiones/{otra}"));
    }

    [Fact]
    public async Task Atajos_1_elige_Subido_y_2_elige_Validado_con_sus_campos()
    {
        var m = Montar();
        var (cut, atajos) = DetallarConAtajos(m, Detalle(SeisPasosNuevos()));

        await cut.InvokeAsync(() => atajos.RecibirAtajo("1"));
        cut.Find("[data-opcion='subido']").GetAttribute("aria-checked").Should().Be("true");
        cut.Find("[data-accion='primario']").TextContent.Should().Be("Guardar y siguiente");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("2"));
        cut.Find("[data-opcion='validado']").GetAttribute("aria-checked").Should().Be("true");
        cut.Find("[data-campos-opcion='validado']").Should().NotBeNull();

        m.Enviados.Should().BeEmpty("elegir una opción no la guarda");
    }

    [Fact]
    public async Task Atajos_1_y_2_respetan_las_opciones_deshabilitadas_por_documento_no_vigente()
    {
        var m = Montar();
        var (cut, atajos) = DetallarConAtajos(m, Detalle(SeisPasosNuevos(), vigente: false));

        await cut.InvokeAsync(() => atajos.RecibirAtajo("1"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("2"));

        cut.FindAll("[data-opcion][aria-checked='true']").Should().BeEmpty();
    }

    [Fact]
    public async Task Atajos_1_no_elige_Subido_en_un_paso_rechazado_y_2_si_elige_Validado()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { Estado = EstadoAcreditacion.Rechazada };
        var m = Montar();
        var (cut, atajos) = DetallarConAtajos(m, Detalle(pasos));

        await cut.InvokeAsync(() => atajos.RecibirAtajo("1"));
        cut.FindAll("[data-opcion][aria-checked='true']").Should().BeEmpty("Subido sobre un rechazo necesita una versión corregida");

        await cut.InvokeAsync(() => atajos.RecibirAtajo("2"));
        cut.Find("[data-opcion='validado']").GetAttribute("aria-checked").Should().Be("true");
    }

    [Fact]
    public async Task Atajos_una_tecla_que_no_es_de_la_pantalla_no_hace_nada()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var (cut, atajos) = DetallarConAtajos(m, Detalle(pasos));

        await cut.InvokeAsync(() => atajos.RecibirAtajo("3"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("Tab"));
        await cut.InvokeAsync(() => atajos.RecibirAtajo("S"));

        PasoAbiertoId(cut).Should().Be(pasos[0].AcreditacionId.ToString());
        cut.FindAll("[data-opcion][aria-checked='true']").Should().BeEmpty();
    }

    [Fact]
    public async Task Atajos_al_salir_de_la_pantalla_se_retira_el_manejador_de_teclado()
    {
        var modulo = JSInterop.SetupModule(ModuloAtajos);
        var suscripcion = modulo.SetupModule(invocacion => invocacion.Identifier == "registrarAtajosPasosGestion");
        suscripcion.SetupVoid("dispose").SetVoidResult();
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));
        cut.WaitForAssertion(() => modulo.Invocations["registrarAtajosPasosGestion"].Should().ContainSingle());

        await DisposeComponentsAsync();

        suscripcion.VerifyInvoke("dispose", 1);
    }
}

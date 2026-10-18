using Bunit;
using CaeManager.Application.Trabajadores.Commands.CrearTrabajador;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Trabajadores.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;

namespace CaeManager.Web.Tests;

/// <summary>
/// El alta de Trabajador en el panel de la maqueta «Panel Formulario TALVEG»: tres pasos (Lo básico, Trabajo, Contacto) que
/// ordenan los mismos campos de siempre. Se fija lo que el panel tenía en main (campos, acciones, un solo comando al guardar)
/// para que el reparto en pasos no pierda ni cambie nada: solo estructura. Quién puede abrir el alta (el rol Consulta no ve
/// «+ Nuevo trabajador») lo fijan <c>TrabajadoresListaGen2Tests.PorRol</c>.
/// </summary>
public partial class TrabajadoresListaGen2Tests
{
    private static IRenderedComponent<DrawerFormulario> PanelDeAlta(IRenderedComponent<Trabajadores> cut) =>
        cut.FindComponents<DrawerFormulario>().Single(d => d.Instance.Titulo == "Nuevo trabajador");

    private static string[] BotonesDelPanel(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll(".drawer-pie button").Select(b => b.TextContent.Trim()).ToArray();

    private static string[] PasosDelPanel(IRenderedComponent<Trabajadores> cut) =>
        cut.FindAll(".drawer-pasos .indicador-pasos-titulo").Select(t => t.TextContent.Trim()).ToArray();

    private static Task PulsarEnElPanelAsync(IRenderedComponent<Trabajadores> cut, string texto) =>
        cut.FindAll(".drawer-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Alta_en_pasos_reparte_los_campos_de_main_sin_perder_ninguno()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);

        cut.Find(".drawer-panel h2").TextContent.Trim().Should().Be("Nuevo trabajador");
        PasosDelPanel(cut).Should().Equal("Lo básico", "Trabajo", "Contacto");

        // Paso 1, Lo básico: empleador (radio + selector), documento, nombre, apellidos, alias y fecha de nacimiento.
        cut.FindAll(".drawer-panel input[name='tipo-empleador']").Should().HaveCount(2);
        PanelDeAlta(cut).FindComponents<CampoSelect>().Should().ContainSingle();
        PanelDeAlta(cut).FindComponents<CampoTexto>().Should().HaveCount(5);
        BotonesDelPanel(cut).Should().Equal("Cancelar", "Guardar y terminar", "Siguiente");

        // Paso 2, Trabajo: puesto, tipo de contratación y observaciones.
        await PulsarEnElPanelAsync(cut, "Siguiente");
        PanelDeAlta(cut).FindComponents<CampoTexto>().Should().ContainSingle();
        PanelDeAlta(cut).FindComponents<CampoSelect>().Should().ContainSingle();
        PanelDeAlta(cut).FindComponents<CampoTextarea>().Should().ContainSingle();
        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar y terminar", "Siguiente");

        // Paso 3, Contacto: correo y teléfono. El último paso termina con «Guardar».
        await PulsarEnElPanelAsync(cut, "Siguiente");
        PanelDeAlta(cut).FindComponents<CampoTexto>().Select(c => c.Instance.Type).Should().Equal("email", "tel");
        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar");
        cut.FindAll(".drawer-pie .boton-primario").Should().ContainSingle().Which.TextContent.Trim().Should().Be("Guardar");

        // Atrás vuelve sin perder el paso anterior; la X y Cancelar siguen preguntando (guardián del kit).
        await PulsarEnElPanelAsync(cut, "Atrás");
        PasosDelPanel(cut).Should().Equal("Lo básico", "Trabajo", "Contacto");
        BotonesDelPanel(cut).Should().Equal("Atrás", "Guardar y terminar", "Siguiente");
    }

    [Fact]
    public async Task Guardar_en_el_ultimo_paso_envia_un_unico_comando_con_lo_escrito_en_todos_los_pasos()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);
        await PulsarEnElPanelAsync(cut, "Siguiente");
        await PanelDeAlta(cut).FindComponents<CampoTexto>().Single().Find("input").InputAsync(new ChangeEventArgs { Value = "Soldador" });
        await PulsarEnElPanelAsync(cut, "Siguiente");
        await PanelDeAlta(cut).FindComponents<CampoTexto>().Single(c => c.Instance.Type == "email").Find("input")
            .InputAsync(new ChangeEventArgs { Value = "carla@ebro.example" });

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().BeEmpty("«Siguiente» no guarda");
        await PulsarEnElPanelAsync(cut, "Guardar");

        var enviada = mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle("el alta sigue siendo un solo comando").Subject;
        enviada.Nombre.Should().Be("Carla");
        enviada.Puesto.Should().Be("Soldador", "lo escrito en el paso 2 viaja en el mismo comando");
        enviada.Email.Should().Be("carla@ebro.example", "lo escrito en el paso 3 también");
    }

    [Fact]
    public async Task Guardar_y_terminar_desde_el_primer_paso_guarda_lo_escrito_hasta_ahi()
    {
        var mediador = new MediatorFalso();
        var cut = Renderizar(mediador);
        await AbrirAltaAsync(cut);
        await cut.Find(".drawer-panel select").ChangeAsync(new ChangeEventArgs { Value = EmpresaEbro.ToString() });
        await EscribirDatosCompletosDelAltaAsync(cut);

        await PulsarEnElPanelAsync(cut, "Guardar y terminar");

        mediador.Enviadas.OfType<CrearTrabajadorCommand>().Should().ContainSingle();
        cut.WaitForAssertion(() => cut.FindAll(".drawer-panel").Should().BeEmpty());
    }

    [Fact]
    public async Task Guardar_sin_empleador_desde_un_paso_posterior_devuelve_al_paso_donde_se_elige()
    {
        var cut = Renderizar(new MediatorFalso());
        await AbrirAltaAsync(cut);
        await PulsarEnElPanelAsync(cut, "Siguiente");

        await PulsarEnElPanelAsync(cut, "Guardar y terminar");

        cut.Find(".drawer-panel .alerta-formulario").TextContent.Trim().Should().Be("Selecciona una empresa.");
        cut.FindAll(".drawer-panel input[name='tipo-empleador']").Should().HaveCount(2, "vuelve a Lo básico, donde está el empleador");
    }
}
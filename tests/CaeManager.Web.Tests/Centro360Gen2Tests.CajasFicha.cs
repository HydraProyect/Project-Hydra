using Bunit;
using CaeManager.Application.Centros.Commands.EditarFichaCentro;
using CaeManager.Application.Common;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Centros.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

public partial class Centro360Gen2Tests
{
    private (Guid Id, MediatorFalso Mediador) FichaCompleta()
    {
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro de prueba") with
        {
            FichaVisible = true,
            HorarioAcceso = "7 a 19",
            ControlAccesosNombre = "Persona accesos",
            ControlAccesosTelefono = "100",
            TecnicoPrevencionNombre = "Persona prevención",
            TecnicoPrevencionTelefono = "200",
            TecnicoPrevencionCorreo = "prl@example.test",
            TrabajosEnAltura = true,
            EspaciosConfinados = false,
            AtmosferasExplosivas = null
        };
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=ficha");
        return (id, mediador);
    }

    private static Task GuardarFichaCentro(IRenderedComponent<EditarFichaCentroDrawer> drawer) =>
        drawer.FindAll("button").Single(b => b.TextContent.Trim() == "Guardar").ClickAsync(new MouseEventArgs());

    [Fact]
    public void Las_cajas_separan_acceso_y_prevencion_y_no_inventan_los_datos_ausentes()
    {
        var (id, _) = FichaCompleta();
        var cut = Renderizar(id);
        var acceso = cut.Find("[data-caja=acceso-centro]").TextContent;
        acceso.Should().Contain("Pol. Norte 4").And.Contain("7 a 19").And.Contain("Persona accesos").And.Contain("100");
        var prevencion = cut.Find("[data-caja=prevencion-centro]").TextContent;
        prevencion.Should().Contain("Persona prevención").And.Contain("200").And.Contain("prl@example.test");
        cut.FindComponents<DatoRejilla>().Single(c => c.Instance.Etiqueta == "Trabajos en altura").Instance.Valor.Should().Be("Sí");
        cut.FindComponents<DatoRejilla>().Single(c => c.Instance.Etiqueta == "Espacios confinados").Instance.Valor.Should().Be("No");
        prevencion.Should().NotContain("Atmósferas explosivas").And.NotContain("Riesgos").And.NotContain("Recurso preventivo");
    }

    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void Editar_solo_se_ofrece_a_los_roles_de_escritura(string rol, bool escritura)
    {
        this.ConRolDeEscritura(rol);
        var (id, _) = FichaCompleta();
        var cut = Renderizar(id);
        cut.FindAll("[data-caja=acceso-centro] .tarjeta-acciones button").Should().HaveCount(escritura ? 1 : 0);
        cut.FindComponents<EditarFichaCentroDrawer>().Should().HaveCount(escritura ? 1 : 0);
    }

    [Fact]
    public void La_ficha_reservada_no_se_muestra_ni_se_edita_si_el_lector_no_la_autoriza()
    {
        var (id, mediador) = FichaCompleta();
        mediador.Detalles[id] = mediador.Detalles[id] with { FichaVisible = false };
        var cut = Renderizar(id);
        cut.Markup.Should().NotContain("Persona accesos").And.NotContain("prl@example.test");
        cut.FindAll("[data-caja=prevencion-centro]").Should().BeEmpty();
        cut.FindComponents<EditarFichaCentroDrawer>().Should().BeEmpty();
    }

    [Fact]
    public async Task El_Drawer_precarga_y_envia_toda_la_ficha_con_la_version_original_y_refresca_las_cajas()
    {
        var (id, mediador) = FichaCompleta();
        var detalle = mediador.Detalles[id];
        mediador.DetalleTrasGuardarFicha = detalle with { HorarioAcceso = "8 a 20" };
        var cut = Renderizar(id);
        await cut.Find("[data-caja=acceso-centro] .tarjeta-acciones button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarFichaCentroDrawer>();
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeFalse();
        await GuardarFichaCentro(drawer);
        mediador.Enviadas.OfType<EditarFichaCentroCommand>().Should().ContainSingle().Subject.Should().BeEquivalentTo(
            new EditarFichaCentroCommand(id, detalle.Version, "7 a 19", "Persona accesos", "100", "Persona prevención", "200", "prl@example.test", true, false, null));
        cut.Find("[data-caja=acceso-centro]").TextContent.Should().Contain("8 a 20");
        drawer.FindAll("[role=dialog]").Should().BeEmpty();
    }

    [Fact]
    public async Task El_conflicto_conserva_el_borrador_y_la_version_original_al_reintentar()
    {
        var (id, mediador) = FichaCompleta();
        var version = mediador.Detalles[id].Version;
        mediador.ResultadoFicha = Result.Fallo(Error.Crear(ConcurrenciaOptimista.CodigoConflicto, "Otra persona ha guardado cambios."));
        var cut = Renderizar(id);
        await cut.Find("[data-caja=acceso-centro] .tarjeta-acciones button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarFichaCentroDrawer>();
        var campo = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Valor == "7 a 19");
        await campo.Find("input").InputAsync(new ChangeEventArgs { Value = "Horario en borrador" });
        await campo.Find("input").BlurAsync(new FocusEventArgs());
        await GuardarFichaCentro(drawer);
        await GuardarFichaCentro(drawer);
        mediador.Enviadas.OfType<EditarFichaCentroCommand>().Should().HaveCount(2)
            .And.OnlyContain(c => c.Version == version && c.HorarioAcceso == "Horario en borrador");
        drawer.FindComponent<DrawerFormulario>().Instance.HayCambios().Should().BeTrue();
        drawer.Markup.Should().Contain("Otra persona ha guardado cambios.");
    }

    [Fact]
    public async Task El_correo_invalido_muestra_el_error_en_el_campo_y_no_envia_el_comando()
    {
        var (id, mediador) = FichaCompleta();
        var cut = Renderizar(id);
        await cut.Find("[data-caja=acceso-centro] .tarjeta-acciones button").ClickAsync(new MouseEventArgs());
        var drawer = cut.FindComponent<EditarFichaCentroDrawer>();
        var campo = drawer.FindComponents<CampoTexto>().Single(c => c.Instance.Valor == "prl@example.test");
        await campo.Find("input").InputAsync(new ChangeEventArgs { Value = "correo-invalido" });
        await campo.Find("input").BlurAsync(new FocusEventArgs());
        await GuardarFichaCentro(drawer);
        campo.Instance.MensajeError.Should().NotBeNullOrWhiteSpace();
        mediador.Enviadas.OfType<EditarFichaCentroCommand>().Should().BeEmpty();
    }

    [Fact]
    public async Task Una_ficha_vacia_se_puede_comenzar_desde_el_menu_del_Centro()
    {
        var (id, mediador) = FichaCompleta();
        mediador.Detalles[id] = mediador.Detalles[id] with
        {
            CodigoCentro = null,
            Direccion = null,
            Contacto = null,
            HorarioAcceso = null,
            ControlAccesosNombre = null,
            ControlAccesosTelefono = null,
            TecnicoPrevencionNombre = null,
            TecnicoPrevencionTelefono = null,
            TecnicoPrevencionCorreo = null,
            TrabajosEnAltura = null,
            EspaciosConfinados = null
        };
        var cut = Renderizar(id);
        cut.FindAll("[role=tab][aria-controls$='-ficha']").Should().ContainSingle(
            "quien puede leer la ficha ve sus cajas aunque estén sin configurar (decisión de producto 2026-10-16)");
        await cut.Find(".cabecera-identidad .menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        var accion = cut.FindComponents<ItemMenuAccion>().Single(c => c.Markup.Contains("Editar acceso y prevención"));
        await cut.InvokeAsync(() => accion.Instance.OnClick.InvokeAsync());
        cut.FindComponent<EditarFichaCentroDrawer>().Instance.Visible.Should().BeTrue();
    }

    [Fact]
    public void La_pestana_Visitas_conserva_todas_las_visitas_activas_sin_fusionar_sus_intervalos()
    {
        var (id, mediador) = FichaCompleta();
        mediador.Visitas[id] = [new(Guid.NewGuid(), new(2026, 12, 20), new(2026, 12, 22), 3),
            new(Guid.NewGuid(), new(2026, 12, 12), new(2026, 12, 14), 2), new(Guid.NewGuid(), new(2026, 12, 13), new(2026, 12, 15), 1)];
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=visitas");
        var cut = Renderizar(id);
        cut.FindAll(".cuerpo-con-lateral-principal .elemento-lista-lateral-etiqueta").Select(x => x.TextContent.Trim())
            .Should().Equal("12/12/2026 – 14/12/2026", "13/12/2026 – 15/12/2026", "20/12/2026 – 22/12/2026");
    }

    // Decisión de producto (2026-10-16): un Centro recién creado no parece configurado. Las dos cajas
    // muestran un estado vacío con «Configurar» y ningún trabajo especial aparece como «No».
    [Theory]
    [InlineData(Roles.GestorCae, true)]
    [InlineData(Roles.Consulta, false)]
    public void Un_Centro_sin_ficha_muestra_las_cajas_vacias_con_Configurar_y_sin_ningun_No(string rol, bool escritura)
    {
        this.ConRolDeEscritura(rol);
        var id = Guid.NewGuid();
        var mediador = Registrar(new MediatorFalso());
        mediador.Detalles[id] = Detalle(id, "Centro de prueba") with { FichaVisible = true, Direccion = null };
        Services.GetRequiredService<NavigationManager>().NavigateTo($"centros/{id}?pestana=ficha");

        var cut = Renderizar(id);

        var acceso = cut.Find("[data-caja=acceso-centro]");
        var prevencion = cut.Find("[data-caja=prevencion-centro]");
        acceso.TextContent.Should().Contain("Acceso sin configurar");
        prevencion.TextContent.Should().Contain("Prevención sin configurar");
        acceso.QuerySelectorAll("button").Count(b => b.TextContent.Trim() == "Configurar").Should().Be(escritura ? 1 : 0);
        prevencion.QuerySelectorAll("button").Count(b => b.TextContent.Trim() == "Configurar").Should().Be(escritura ? 1 : 0);
        cut.FindAll("[data-caja=acceso-centro] .tarjeta-acciones button, [data-caja=prevencion-centro] .tarjeta-acciones button")
            .Should().BeEmpty("sin datos no hay nada que editar: la acción es Configurar");
        cut.FindComponents<DatoRejilla>().Select(c => c.Instance.Valor).Should().NotContain("No").And.NotContain("Sí");
    }
}

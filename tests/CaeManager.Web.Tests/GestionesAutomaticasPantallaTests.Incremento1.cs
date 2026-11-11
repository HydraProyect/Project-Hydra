using Bunit;
using CaeManager.Application.Documentos.Commands.MarcarAcreditacionNoRequerida;
using CaeManager.Application.Gestiones.Queries.GestionesAutomaticas;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadorPorId;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Incremento 1 de la ventana de la Gestión (Gestion Pasos por tipo TALVEG.dc.html, G-3/G-4): el CIF de la Empresa
/// gestionada en la cabecera, «Datos a mano» (pestañas Trabajador y Centro) y «Esta plataforma no lo pide» con su
/// confirmación y su comando. Cada elemento y estado, por rol.
/// </summary>
public partial class GestionesAutomaticasPantallaTests
{
    private static DatosTrabajadorDeGestionDto TrabajadorCompleto(DateOnly? baja = null) => new(
        TrabajadorId, "Laura Gil Pardo", "73215489-k", new DateOnly(1988, 2, 21), "501234567890", "Española",
        "Técnica de mantenimiento", "Indefinido", new DateOnly(2019, 3, 1), "Sí", "No necesita", baja, PermisoTrabajoVisible: true);

    private static DatosTrabajadorDeGestionDto TrabajadorIncompleto() => new(
        TrabajadorId, "Laura Gil Pardo", "73215489K", null, null, "Española", "Técnica de mantenimiento", null, null, null, null, null,
        PermisoTrabajoVisible: true);

    private static List<PasoGestionAutomaticaDto> PasosConCentroCompleto()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { CentroCodigo = "PN-01", CentroDireccion = "Polígono Malpica, calle E, 50016 Zaragoza", InicioAsignacion = new DateOnly(2026, 9, 1) };
        return pasos;
    }

    private static Dictionary<string, string> Datos(IRenderedComponent<GestionAutomaticaDetalle> cut) =>
        cut.FindAll("[data-datos-a-mano] [data-dato]").ToDictionary(
            d => d.GetAttribute("data-dato")!,
            d => d.QuerySelector("[data-falta]") is not null ? "FALTA" : d.QuerySelector(".gp-campo-clave + *")!.TextContent.Trim());

    // ---------- cabecera: CIF de la Empresa gestionada ----------

    [Fact]
    public void La_cabecera_enseña_el_CIF_de_la_Empresa_gestionada_junto_a_su_nombre_y_se_copia()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()) with { EmpresaGestionadaCif = "B87654323" });

        var kicker = cut.Find("[data-empresa-gestionada]");
        kicker.TextContent.Should().StartWith("Construcciones Ebro S.L. · ").And.Contain("B87654323");
        var cif = kicker.QuerySelector("[data-cif-empresa] button")!;
        cif.TextContent.Trim().Should().Be("B87654323");
        cif.GetAttribute("aria-label").Should().Be("Copiar CIF");
    }

    [Fact]
    public void Sin_CIF_la_cabecera_enseña_solo_el_nombre()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-empresa-gestionada]").TextContent.Should().Be("Construcciones Ebro S.L.");
        cut.FindAll("[data-cif-empresa]").Should().BeEmpty();
    }

    // ---------- Datos a mano ----------

    [Fact]
    public void Datos_a_mano_abre_en_la_pestaña_del_Trabajador_con_sus_datos_en_el_formato_de_los_portales()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(PasosConCentroCompleto()) with { Trabajador = TrabajadorCompleto(baja: new DateOnly(2026, 12, 31)) });

        cut.Find("[data-datos-a-mano] .gp-etiqueta").TextContent.Should().Be("Datos a mano");
        cut.Find("[data-datos-a-mano] [role='tablist']").GetAttribute("aria-label").Should().Be("Datos");
        var pestanas = cut.FindAll("[data-datos-a-mano] [role='tab']");
        pestanas.Select(p => p.GetAttribute("data-pestana")).Should().Equal("trabajador", "centro");
        pestanas.Select(p => p.GetAttribute("aria-selected")).Should().Equal("true", "false");
        pestanas[0].TextContent.Trim().Should().Be("Trabajador", "sin datos que falten no hay cuenta");

        Datos(cut).Should().Equal(new Dictionary<string, string>
        {
            ["GpDatoNombre"] = "Laura Gil Pardo",
            ["GpDatoDni"] = "73215489K",
            ["GpDatoNacimiento"] = "21/02/1988",
            ["GpDatoNaf"] = "501234567890",
            ["GpDatoNacionalidad"] = "Española",
            ["GpDatoPuesto"] = "Técnica de mantenimiento",
            ["GpDatoContratacion"] = "Indefinido",
            ["GpDatoAltaEmpresa"] = "01/03/2019",
            ["GpDatoRnt"] = "Sí",
            ["GpDatoPermiso"] = "No necesita",
            ["GpDatoBaja"] = "31/12/2026"
        });
        cut.Find("[data-dato='GpDatoDni'] .gp-campo-clave").TextContent.Should().Be("DNI / NIE");
        cut.Find("[data-dato='GpDatoDni'] button").GetAttribute("aria-label").Should().Be("Copiar DNI / NIE");
        cut.Find("[data-datos-nota]").TextContent.Should().Be(
            "Un clic en un dato lo copia con el formato que piden los portales. «Completar» abre la ficha sin salir de la Gestión.");
    }

    [Fact]
    public void Sin_fecha_de_baja_no_sale_la_fila_de_baja_porque_no_es_un_dato_que_falte()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()) with { Trabajador = TrabajadorCompleto() });

        Datos(cut).Keys.Should().NotContain("GpDatoBaja");
        cut.FindAll("[data-datos-a-mano] [data-falta]").Should().BeEmpty();
    }

    [Fact]
    public void Lo_que_falta_del_Trabajador_se_cuenta_en_su_pestaña_y_Completar_abre_su_ficha_sin_salir_de_la_Gestion()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()) with { Trabajador = TrabajadorIncompleto() });

        var faltan = new[] { "GpDatoNacimiento", "GpDatoNaf", "GpDatoContratacion", "GpDatoAltaEmpresa", "GpDatoRnt", "GpDatoPermiso" };
        Datos(cut).Where(d => d.Value == "FALTA").Select(d => d.Key).Should().BeEquivalentTo(faltan);
        cut.Find("[data-pestana='trabajador'] [data-faltan]").TextContent.Should().Be("· 6");
        cut.Find("[data-dato='GpDatoNaf'] [data-falta]").TextContent.Should().Contain("Falta en TALVEG").And.Contain("Completar");
        cut.Find("[data-dato='GpDatoNaf'] [data-completar]").GetAttribute("aria-label").Should().Be("Completar Nº de afiliación (NAF)");
        cut.FindAll(".drawer-formulario, [data-drawer]").Should().BeEmpty();

        cut.Find("[data-dato='GpDatoNaf'] [data-completar]").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<ObtenerTrabajadorPorIdQuery>().Should().ContainSingle()
            .Which.Id.Should().Be(TrabajadorId));
        cut.FindAll("[data-paso-abierto]").Should().NotBeEmpty("Completar no saca de la Gestión");
        m.DetallesPedidos.Should().ContainSingle("abrir la ficha no vuelve a leer la Gestión; solo guardarla");
    }

    [Fact]
    public void Sin_permiso_de_trabajo_visible_no_sale_su_fila_ni_cuenta_como_dato_que_falta_ni_ofrece_Completar()
    {
        // El servidor lo manda null cuando quien consulta no lee la nota interna: no es «Falta en TALVEG».
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()) with { Trabajador = TrabajadorIncompleto() with { PermisoTrabajoVisible = false } });

        Datos(cut).Keys.Should().NotContain("GpDatoPermiso");
        cut.FindAll("[data-dato='GpDatoPermiso']").Should().BeEmpty();
        cut.Find("[data-pestana='trabajador'] [data-faltan]").TextContent.Should().Be("· 5");
    }

    [Fact]
    public void La_pestaña_Centro_enseña_nombre_codigo_direccion_inicio_de_la_asignacion_y_acceso()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(PasosConCentroCompleto()) with { Trabajador = TrabajadorCompleto() });

        cut.Find("[data-pestana='centro']").Click();

        cut.FindAll("[data-datos-a-mano] [role='tab']").Select(p => p.GetAttribute("aria-selected")).Should().Equal("false", "true");
        cut.Find("[data-pestana-abierta]").GetAttribute("data-pestana-abierta").Should().Be("centro");
        Datos(cut).Should().Equal(new Dictionary<string, string>
        {
            ["GpDatoCentro"] = "Planta Norte",
            ["GpDatoCodigoCentro"] = "PN-01",
            ["GpDatoDireccion"] = "Polígono Malpica, calle E, 50016 Zaragoza",
            ["GpDatoInicioAsignacion"] = "01/09/2026",
            ["GpDatoAcceso"] = "Nalanda"
        });
    }

    [Fact]
    public void Sin_Trabajador_en_el_detalle_solo_hay_pestaña_Centro_y_lo_que_falta_se_completa_en_Centro_360()
    {
        var m = Montar();
        var pasos = SeisPasosNuevos();
        var cut = Detallar(m, Detalle(pasos));

        cut.FindAll("[data-datos-a-mano] [role='tab']").Select(p => p.GetAttribute("data-pestana")).Should().Equal("centro");
        Datos(cut).Should().Equal(new Dictionary<string, string>
        {
            ["GpDatoCentro"] = "Planta Norte",
            ["GpDatoCodigoCentro"] = "FALTA",
            ["GpDatoDireccion"] = "FALTA",
            ["GpDatoAcceso"] = "Nalanda"
        }, "sin Asignación no hay fecha de inicio que enseñar, y eso no es un dato que falte");
        cut.Find("[data-pestana='centro'] [data-faltan]").TextContent.Should().Be("· 2");
        cut.Find("[data-dato='GpDatoCodigoCentro'] a[data-completar]").GetAttribute("href").Should().Be($"centros/{pasos[0].CentroId}");
        cut.Find("[data-datos-nota]").TextContent.Should().Be(
            "Un clic en un dato lo copia con el formato que piden los portales. «Completar» abre Centro 360, fuera de la Gestión.",
            "en esta pestaña «Completar» sí sale de la Gestión, y la nota no puede decir lo contrario");
    }

    [Fact]
    public void Al_cambiar_de_paso_los_datos_del_Centro_son_los_del_paso_abierto()
    {
        var m = Montar();
        var pasos = PasosConCentroCompleto();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find($"[data-paso='{pasos[1].AcreditacionId}']").Click();

        Datos(cut)["GpDatoCentro"].Should().Be("Almacén Sur");
        Datos(cut)["GpDatoCodigoCentro"].Should().Be("FALTA");
    }

    [Fact]
    public void Rol_Consulta_ve_y_copia_los_datos_pero_no_tiene_Completar()
    {
        var m = Montar(Roles.Consulta);
        var cut = Detallar(m, Detalle(SeisPasosNuevos()) with { Trabajador = TrabajadorIncompleto(), EmpresaGestionadaCif = "B87654323" });

        cut.Find("[data-dato='GpDatoNombre'] button").TextContent.Trim().Should().Be("Laura Gil Pardo");
        cut.FindAll("[data-datos-a-mano] [data-falta]").Should().NotBeEmpty("lo que falta se sigue diciendo");
        cut.FindAll("[data-completar]").Should().BeEmpty();
        cut.Find("[data-pestana='centro']").Click();
        cut.FindAll("[data-completar]").Should().BeEmpty();
        cut.Find("[data-datos-nota]").TextContent.Should().Be("Un clic en un dato lo copia con el formato que piden los portales.");
        cut.Find("[data-cif-empresa] button").TextContent.Trim().Should().Be("B87654323");
    }

    [Theory]
    [InlineData("73215489-k", "73215489K")]
    [InlineData(" x-1234567-l ", "X1234567L")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void El_DNI_se_copia_sin_guiones_ni_espacios_y_en_mayusculas(string? dni, string? esperado) =>
        GestionAutomaticaDetalle.DniParaPortal(dni).Should().Be(esperado);

    // ---------- Esta plataforma no lo pide ----------

    [Fact]
    public void Esta_plataforma_no_lo_pide_exige_confirmar_y_envia_MarcarAcreditacionNoRequerida()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.FindAll("[data-campos-opcion='nopide']").Should().BeEmpty();
        cut.Find("[data-opcion='nopide']").Click();

        cut.Find("[data-opcion='nopide']").GetAttribute("aria-checked").Should().Be("true");
        cut.Find("[data-campos-opcion='nopide']").TextContent.Trim().Should().Be("Confirmo que Nalanda no pide este documento en Planta Norte.");
        cut.Find("[data-accion='guardar']").HasAttribute("disabled").Should().BeTrue("sin la confirmación no se guarda");
        cut.Find("[data-accion='primario']").HasAttribute("disabled").Should().BeTrue();

        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").Change(true);
        cut.Find("[data-accion='guardar']").HasAttribute("disabled").Should().BeFalse();
        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionNoRequeridaCommand>().Should().ContainSingle()
            .Which.Should().Be(new MarcarAcreditacionNoRequeridaCommand(pasos[0].AcreditacionId, pasos[0].VersionAcreditacion),
                "lleva la versión que se tenía delante, para no sobrescribir a ciegas"));
        Toasts.Mensajes.Should().ContainSingle().Which.Tono.Should().Be(TonoToast.Exito);
        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[1].AcreditacionId.ToString());
    }

    [Fact]
    public void La_confirmacion_de_no_lo_pide_no_se_arrastra_al_paso_siguiente()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-opcion='nopide']").Click();
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").Change(true);
        cut.Find("[data-accion='primario']").Click();
        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionNoRequeridaCommand>().Should().ContainSingle());

        cut.Find("[data-opcion='nopide']").Click();

        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").HasAttribute("checked").Should().BeFalse();
        cut.Find("[data-accion='guardar']").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void Cambiar_de_opcion_borra_la_confirmacion_de_no_lo_pide()
    {
        var m = Montar();
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.Find("[data-opcion='nopide']").Click();
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").Change(true);
        cut.Find("[data-opcion='nopide']").Click();
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").HasAttribute("checked").Should()
            .BeTrue("elegir otra vez la misma opción no la borra");

        cut.Find("[data-opcion='subido']").Click();
        cut.Find("[data-opcion='nopide']").Click();

        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").HasAttribute("checked").Should().BeFalse();
        cut.Find("[data-accion='guardar']").HasAttribute("disabled").Should().BeTrue("sin confirmar otra vez no se guarda");
        m.Enviados.OfType<MarcarAcreditacionNoRequeridaCommand>().Should().BeEmpty();
    }

    [Fact]
    public void No_lo_pide_envia_la_version_del_paso_abierto_aunque_no_sea_Pendiente_de_subir()
    {
        var pasos = SeisPasosNuevos();
        pasos[0] = pasos[0] with { Estado = EstadoAcreditacion.Rechazada };
        var m = Montar();
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString(), "precondición");
        cut.Find("[data-opcion='nopide']").Click();
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").Change(true);
        cut.Find("[data-accion='guardar']").Click();

        cut.WaitForAssertion(() => m.Enviados.OfType<MarcarAcreditacionNoRequeridaCommand>().Should().ContainSingle()
            .Which.VersionEsperada.Should().Be(pasos[0].VersionAcreditacion).And.NotBe(Guid.Empty));
    }

    [Theory]
    [InlineData("Acreditacion.CambiadaDesdeQueSeAbrio")]
    // El choque que llega en el guardado (ConcurrenciaBehavior): otra escritura entre la lectura del comando y su SaveChanges.
    [InlineData("Concurrencia.Conflicto")]
    public void Si_el_paso_cambio_desde_que_se_abrio_se_relee_y_hay_que_confirmar_otra_vez_sin_Reintentar_a_ciegas(string codigo)
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        m.FalloAlMarcar = codigo == MarcarAcreditacionNoRequeridaCommandHandler.CambiadaDesdeQueSeAbrio.Codigo
            ? MarcarAcreditacionNoRequeridaCommandHandler.CambiadaDesdeQueSeAbrio.Mensaje
            : "Otra persona modificó este registro mientras lo editabas. Vuelve a abrirlo para ver los cambios y aplica los tuyos de nuevo.";
        m.CodigoFallo = codigo;
        var cut = Detallar(m, Detalle(pasos));
        var ahora = pasos.ToList();
        ahora[0] = ahora[0] with { Estado = EstadoAcreditacion.Rechazada };

        cut.Find("[data-opcion='nopide']").Click();
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").Change(true);
        m.Detalle = Detalle(ahora);
        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => m.DetallesPedidos.Should().HaveCount(2, "el fallo vuelve a leer la Gestión"));
        cut.Find("[data-aviso='error-guardado']").TextContent.Should().Contain("Este paso ha cambiado desde que abriste la Gestión.");
        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString());
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").HasAttribute("checked").Should()
            .BeFalse("se confirma otra vez sobre el estado real");
        cut.FindAll("[data-accion='reintentar']").Should().BeEmpty("reintentar reenviaría el estado viejo");
    }

    [Fact]
    public void Si_el_comando_de_no_lo_pide_falla_se_avisa_y_no_se_avanza()
    {
        var pasos = SeisPasosNuevos();
        var m = Montar();
        m.FalloAlMarcar = "No encontramos ese paso.";
        var cut = Detallar(m, Detalle(pasos));

        cut.Find("[data-opcion='nopide']").Click();
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").Change(true);
        cut.Find("[data-accion='primario']").Click();

        cut.WaitForAssertion(() => cut.Find("[data-aviso='error-guardado']").TextContent
            .Should().Contain("No se ha guardado el paso.").And.Contain("No encontramos ese paso."));
        Toasts.Mensajes.Should().NotContain(t => t.Tono == TonoToast.Exito);
        cut.Find("[data-paso-abierto]").GetAttribute("data-paso-abierto").Should().Be(pasos[0].AcreditacionId.ToString());
        cut.Find("[data-opcion='nopide']").GetAttribute("aria-checked").Should().Be("true", "lo elegido sigue ahí");
        cut.Find("[data-campos-opcion='nopide'] input[type='checkbox']").HasAttribute("checked").Should().BeTrue("la confirmación no se pierde al fallar");
    }

    [Fact]
    public void Rol_Consulta_no_puede_marcar_que_la_plataforma_no_lo_pide()
    {
        var m = Montar(Roles.Consulta);
        var cut = Detallar(m, Detalle(SeisPasosNuevos()));

        cut.FindAll("[data-opcion='nopide']").Should().BeEmpty();
        cut.FindAll("[data-campos-opcion='nopide']").Should().BeEmpty();
        m.Enviados.OfType<MarcarAcreditacionNoRequeridaCommand>().Should().BeEmpty();
    }
}

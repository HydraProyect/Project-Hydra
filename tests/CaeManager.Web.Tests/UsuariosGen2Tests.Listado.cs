using Bunit;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;

namespace CaeManager.Web.Tests;

public partial class UsuariosGen2Tests
{
    [Fact]
    public void El_listado_usa_la_barra_compartida_y_el_pie_separado()
    {
        Sembrar((Cuenta(MartaId, "marta@talveg.es", "Marta"), Roles.Administrador));
        var cut = Renderizar();
        cut.Find(".lista-design").Should().NotBeNull();
        cut.FindComponents<BarraFiltros>().Should().ContainSingle();
        cut.FindComponents<FranjaEstado>().Should().ContainSingle();
        cut.FindComponents<FiltrosGuardadosDeListado>().Should().ContainSingle();
        cut.FindComponents<VistaRecordadaDeListado>().Should().ContainSingle();
        cut.Find(".tarjeta-listado-pie").QuerySelector("table").Should().BeNull();
        cut.Find(".lista-identificador").TextContent.Should().Be("marta@talveg.es");
    }

    [Fact]
    public async Task Agrupar_por_rol_pliega_y_no_corta_los_grupos_por_pagina()
    {
        var cuentas = Enumerable.Range(0, 26).Select(i =>
            (Cuenta(Guid.NewGuid(), $"persona{i}@talveg.es", $"Persona {i}"), Roles.Consulta)).ToArray();
        Sembrar(cuentas);
        var cut = Renderizar();
        await cut.InvokeAsync(() => cut.FindComponent<DesplegableAgrupar>().Instance.ValorChanged.InvokeAsync("rol"));
        cut.FindAll(".lista-identificador").Should().BeEmpty();
        var grupo = cut.FindComponent<CabeceraGrupoLista>();
        grupo.Instance.Total.Should().Be(26);
        await cut.InvokeAsync(() => grupo.Instance.OnAlternar.InvokeAsync());
        cut.FindAll(".lista-identificador").Should().HaveCount(5);
        cut.Find(".fila-restantes").TextContent.Should().Contain("Ver los 21 restantes");
        await cut.Find(".fila-restantes button").ClickAsync(new());
        cut.FindAll(".lista-identificador").Should().HaveCount(26);
        await cut.InvokeAsync(() => grupo.Instance.OnAlternar.InvokeAsync());
        cut.FindAll(".lista-identificador").Should().BeEmpty();
    }

    [Fact]
    public async Task La_franja_filtra_varios_estados_y_cuenta_sin_el_filtro_de_estado()
    {
        var activa = Cuenta(MartaId, "activa@talveg.es", "Activa");
        var pendiente = Cuenta(JonId, "pendiente@talveg.es", "Pendiente");
        pendiente.PasswordHash = null;
        Sembrar((activa, Roles.Consulta), (pendiente, Roles.Consulta));
        var cut = Renderizar();
        await cut.Find("[data-estado=pendientes]").ClickAsync(new());
        cut.FindAll(".lista-identificador").Should().ContainSingle().Which.TextContent.Should().Be("pendiente@talveg.es");
        cut.FindComponent<FranjaEstado>().Instance.Recuentos!["activos"].Should().Be(1);
        await cut.Find("[data-estado=activos]").ClickAsync(new());
        cut.FindAll(".lista-identificador").Should().HaveCount(2);
    }

    [Fact]
    public async Task Seleccionar_visibles_no_incluye_grupos_plegados_y_plegar_limpia_la_seleccion()
    {
        Sembrar((Cuenta(MartaId, "marta@talveg.es", "Marta"), Roles.Administrador),
            (Cuenta(JonId, "jon@talveg.es", "Jon"), Roles.Consulta));
        var cut = Renderizar();
        await cut.InvokeAsync(() => cut.FindComponent<DesplegableAgrupar>().Instance.ValorChanged.InvokeAsync("rol"));
        await cut.InvokeAsync(() => cut.FindComponent<BarraFiltros>().Instance.SeleccionMultipleChanged.InvokeAsync(true));
        var grupos = cut.FindComponents<CabeceraGrupoLista>();
        await cut.InvokeAsync(() => grupos[0].Instance.OnAlternar.InvokeAsync());
        await cut.Find("thead input[type=checkbox]").ChangeAsync(new() { Value = true });
        cut.FindAll("tbody input[type=checkbox]:checked").Should().ContainSingle();
        await cut.InvokeAsync(() => grupos[0].Instance.OnAlternar.InvokeAsync());
        await cut.InvokeAsync(() => grupos[0].Instance.OnAlternar.InvokeAsync());
        cut.FindAll("tbody input[type=checkbox]:checked").Should().BeEmpty();
    }
    [Fact]
    public async Task Aplicar_una_vista_guardada_restaura_rol_estado_busqueda_y_agrupacion_y_quita_los_ausentes()
    {
        Sembrar((Cuenta(MartaId, "marta@talveg.es", "Marta"), Roles.Administrador),
            (Cuenta(JonId, "jon@talveg.es", "Jon"), Roles.Consulta));
        var cut = Renderizar();
        await cut.InvokeAsync(() => cut.FindComponent<FiltrosGuardadosDeListado>().Instance.OnAplicar.InvokeAsync(
            new Dictionary<string, string?> { ["q"] = "Jon", ["rol"] = Roles.Consulta, ["estado"] = "activos", ["agrupar"] = "rol" }));
        cut.FindComponent<BarraFiltros>().Instance.Busqueda.Should().Be("Jon");
        cut.FindComponent<PastillaFiltro>().Instance.Valor.Should().Be(Roles.Consulta);
        cut.FindComponent<FranjaEstado>().Instance.Valor.Should().Be("activos");
        cut.FindComponent<DesplegableAgrupar>().Instance.Valor.Should().Be("rol");
        cut.FindAll(".lista-identificador").Should().BeEmpty("la agrupación restaurada nace plegada");
        await cut.InvokeAsync(() => cut.FindComponent<VistaRecordadaDeListado>().Instance.OnAplicar.InvokeAsync(new Dictionary<string, string?>()));
        cut.FindComponent<BarraFiltros>().Instance.Busqueda.Should().BeEmpty();
        cut.FindComponent<PastillaFiltro>().Instance.Valor.Should().BeEmpty();
        cut.FindComponent<FranjaEstado>().Instance.Valor.Should().BeEmpty();
        cut.FindComponent<DesplegableAgrupar>().Instance.Valor.Should().BeNull();
        cut.FindAll(".lista-identificador").Should().HaveCount(2);
    }

    [Fact]
    public async Task Los_atajos_solo_recorren_y_seleccionan_filas_de_grupos_abiertos()
    {
        Sembrar((Cuenta(MartaId, "marta@talveg.es", "Marta"), Roles.Administrador),
            (Cuenta(JonId, "jon@talveg.es", "Jon"), Roles.Consulta));
        var cut = Renderizar();
        await cut.InvokeAsync(() => cut.FindComponent<DesplegableAgrupar>().Instance.ValorChanged.InvokeAsync("rol"));
        await cut.InvokeAsync(() => cut.FindComponent<BarraFiltros>().Instance.SeleccionMultipleChanged.InvokeAsync(true));
        await Atajo(cut, "j");
        cut.FindAll(".fila-enfocada").Should().BeEmpty();
        var grupo = cut.FindComponents<CabeceraGrupoLista>().Single(g => g.Instance.Nombre == "Consulta");
        await cut.InvokeAsync(() => grupo.Instance.OnAlternar.InvokeAsync());
        await Atajo(cut, "j");
        await Atajo(cut, "x");
        cut.Find(".fila-enfocada").TextContent.Should().Contain("jon@talveg.es");
        cut.FindAll("tbody input[type=checkbox]:checked").Should().ContainSingle();
    }}

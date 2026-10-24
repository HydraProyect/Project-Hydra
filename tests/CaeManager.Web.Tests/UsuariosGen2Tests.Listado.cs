using ApplicationUser = CaeManager.Infrastructure.Identity.ApplicationUser;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
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
        cut.Find(".tarjeta-listado-pie-separado").QuerySelector("table").Should().BeNull();
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
        cut.FindComponent<FranjaEstado>().Instance.Recuentos!["activos"].Should().Be(2);
        await cut.Find("[data-estado=activos]").ClickAsync(new());
        cut.FindAll(".lista-identificador").Should().HaveCount(2);
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
    public async Task Los_atajos_solo_recorren_filas_de_grupos_abiertos_y_no_hay_seleccion()
    {
        Sembrar((Cuenta(MartaId, "marta@talveg.es", "Marta"), Roles.Administrador),
            (Cuenta(JonId, "jon@talveg.es", "Jon"), Roles.Consulta));
        var cut = Renderizar();
        await cut.InvokeAsync(() => cut.FindComponent<DesplegableAgrupar>().Instance.ValorChanged.InvokeAsync("rol"));
        await Atajo(cut, "j");
        cut.FindAll(".fila-enfocada").Should().BeEmpty();
        var grupo = cut.FindComponents<CabeceraGrupoLista>().Single(g => g.Instance.Nombre == "Consulta");
        await cut.InvokeAsync(() => grupo.Instance.OnAlternar.InvokeAsync());
        await Atajo(cut, "j");
        await Atajo(cut, "x");
        cut.Find(".fila-enfocada").TextContent.Should().Contain("jon@talveg.es");
        cut.FindAll("tbody input[type=checkbox]").Should().BeEmpty();
        cut.FindAll("[data-keytip=S]").Should().BeEmpty();
    }
    [Fact]
    public void El_estado_Activo_va_con_punto_sin_pastilla_y_Desactivado_con_pastilla_neutra()
    {
        // Maqueta Usuarios TALVEG: lo correcto no lleva pastilla; lo desactivado, pastilla neutra (no roja).
        Sembrar((Cuenta(MartaId, "activa@talveg.es", "Activa"), Roles.Consulta),
            (Cuenta(JonId, "baja@talveg.es", "Baja", activa: false), Roles.Consulta));
        var cut = Renderizar();
        var activa = Fila(cut, "activa@talveg.es");
        activa.QuerySelector(".estado-fila-correcto")!.TextContent.Trim().Should().Be("Activo");
        activa.QuerySelector(".badge").Should().BeNull();
        var baja = Fila(cut, "baja@talveg.es");
        baja.QuerySelector(".estado-fila-correcto").Should().BeNull();
        baja.QuerySelector(".badge-neutro")!.TextContent.Trim().Should().Be("Desactivado");
    }
    [Fact]
    public async Task Una_cuenta_pendiente_y_desactivada_pertenece_a_ambos_chips()
    {
        Sembrar((Cuenta(MartaId, "pendiente@talveg.es", "Pendiente", activa: false, pendienteActivacion: true), Roles.Consulta),
            (Cuenta(JonId, "activa@talveg.es", "Activa", pendienteActivacion: true), Roles.Consulta));
        var cut = Renderizar(url: "usuarios?estado=activos");
        cut.FindAll(".lista-identificador").Should().ContainSingle().Which.TextContent.Should().Be("activa@talveg.es");
        await cut.Find("[data-estado=activos]").ClickAsync(new());
        await cut.Find("[data-estado=desactivados]").ClickAsync(new());
        cut.FindAll(".lista-identificador").Should().ContainSingle().Which.TextContent.Should().Be("pendiente@talveg.es");
        var franja = cut.FindComponent<FranjaEstado>().Instance;
        franja.Recuentos!["desactivados"].Should().Be(1);
        franja.Recuentos["activos"].Should().Be(1);
        franja.Recuentos["pendientes"].Should().Be(2);
        franja.TotalTodos.Should().Be(2, "las pertenencias se solapan y no se suman para Todos");
        await cut.Find("[data-estado=pendientes]").ClickAsync(new());
        cut.FindAll(".lista-identificador").Should().HaveCount(2);
    }

    [Fact]
    public async Task Los_recuentos_respetan_rol_y_busqueda_y_omiten_solo_el_filtro_de_estado()
    {
        Sembrar((Cuenta(MartaId, "ana1@talveg.es", "Ana activa"), Roles.Consulta),
            (Cuenta(JonId, "ana2@talveg.es", "Ana pendiente", activa: false, pendienteActivacion: true), Roles.Consulta),
            (Cuenta(AnderId, "ana3@talveg.es", "Ana otro rol"), Roles.Administrador),
            (Cuenta(IkerId, "otro@talveg.es", "Otro nombre"), Roles.Consulta));
        var cut = Renderizar(url: "usuarios?q=Ana&rol=" + Uri.EscapeDataString(Roles.Consulta) + "&estado=desactivados");
        cut.FindAll(".lista-identificador").Should().ContainSingle().Which.TextContent.Should().Be("ana2@talveg.es");
        var franja = cut.FindComponent<FranjaEstado>().Instance;
        franja.Recuentos!["activos"].Should().Be(1);
        franja.Recuentos["desactivados"].Should().Be(1);
        franja.Recuentos["pendientes"].Should().Be(1);
        franja.TotalTodos.Should().Be(2);
        cut.FindAll(".chip-filtro").Select(c => c.TextContent).Should().Contain(c => c.Contains("Rol: Consulta"));
        cut.FindAll(".chip-filtro").Select(c => c.TextContent).Should().Contain(c => c.Contains("Búsqueda: Ana"));
        cut.Find("[data-filtro-pantalla]").GetAttribute("placeholder").Should().Be("Filtrar esta pantalla: nombre o correo");
        await cut.Find("[data-estado=desactivados]").ClickAsync(new());
        cut.FindAll(".lista-identificador").Should().HaveCount(2);
    }

    [Fact]
    public async Task Agrupar_se_restaura_desde_la_URL_y_forma_parte_de_la_lista_blanca()
    {
        Sembrar((Cuenta(MartaId, "marta@talveg.es", "Marta"), Roles.Consulta));
        var cut = Renderizar(url: "usuarios?agrupar=rol");
        cut.FindComponent<DesplegableAgrupar>().Instance.Valor.Should().Be("rol");
        cut.FindComponent<VistaRecordadaDeListado>().Instance.ParametrosDeVista.Should().Contain("agrupar");
        cut.FindComponent<FiltrosGuardadosDeListado>().Instance.ParametrosDeVista.Should().Contain("agrupar");
        cut.FindAll(".lista-identificador").Should().BeEmpty();
        await cut.InvokeAsync(() => cut.FindComponent<DesplegableAgrupar>().Instance.ValorChanged.InvokeAsync(null));
        Services.GetRequiredService<NavigationManager>().Uri.Should().NotContain("agrupar=");
        await cut.InvokeAsync(() => cut.FindComponent<DesplegableAgrupar>().Instance.ValorChanged.InvokeAsync("rol"));
        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("agrupar=rol");
    }

    [Theory]
    [InlineData("usuarios?rol=Inventado&estado=Inventado&agrupar=Inventado", "")]
    [InlineData("usuarios?rol=Inventado&estado=activos,Inventado&agrupar=Inventado", "activos")]
    public void Los_enlaces_profundos_descartan_valores_invalidos(string url, string estado)
    {
        Sembrar((Cuenta(MartaId, "marta@talveg.es", "Marta"), Roles.Consulta));
        var cut = Renderizar(url: url);
        cut.FindComponent<PastillaFiltro>().Instance.Valor.Should().BeEmpty();
        cut.FindComponent<FranjaEstado>().Instance.Valor.Should().Be(estado);
        cut.FindComponent<DesplegableAgrupar>().Instance.Valor.Should().BeNull();
        cut.FindAll(".lista-identificador").Should().ContainSingle();
    }

    [Fact]
    public async Task Reactivar_y_recargar_conserva_el_grupo_abierto_y_los_restantes_desplegados()
    {
        var cuentas = Enumerable.Range(0, 7).Select(i =>
            (Cuenta(Guid.NewGuid(), $"persona{i}@talveg.es", $"Persona {i}", activa: false), Roles.Consulta)).ToArray();
        Sembrar(cuentas);
        var cut = Renderizar(url: "usuarios?agrupar=rol");
        await cut.InvokeAsync(() => cut.FindComponent<CabeceraGrupoLista>().Instance.OnAlternar.InvokeAsync());
        await cut.Find(".fila-restantes button").ClickAsync(new());
        await PulsarEnMenuAsync(cut, "persona0@talveg.es", "Reactivar");
        cut.WaitForAssertion(() => cut.FindAll(".lista-identificador").Should().HaveCount(7));
        cut.FindComponent<CabeceraGrupoLista>().Instance.Abierto.Should().BeTrue();
        await cut.InvokeAsync(() => cut.Instance.RecargarAsync());
        cut.FindAll(".lista-identificador").Should().HaveCount(7);
    }

    [Fact]
    public async Task El_modo_equipo_aplica_los_filtros_y_la_agrupacion_solo_sobre_su_equipo()
    {
        SembrarEquipoYOtros();
        var cut = Renderizar(actorId: CoordinadoraId, rolActor: Roles.CoordinadorCae,
            url: "usuarios?q=Ander&rol=" + Uri.EscapeDataString(Roles.GestorCae) + "&agrupar=rol");
        cut.FindComponents<CabeceraGrupoLista>().Should().ContainSingle().Which.Instance.Total.Should().Be(1);
        await cut.InvokeAsync(() => cut.FindComponent<CabeceraGrupoLista>().Instance.OnAlternar.InvokeAsync());
        cut.FindAll(".lista-identificador").Should().ContainSingle().Which.TextContent.Should().Be("a.beitia@talveg.es");
        cut.FindComponent<FranjaEstado>().Instance.TotalTodos.Should().Be(1);
        cut.FindAll("[data-keytip=S]").Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void No_hay_pie_ni_paginador_en_lista_vacia_o_error(bool error)
    {
        if (error) _fuente.Visibles = _ => Task.FromException<IReadOnlyList<ApplicationUser>>(new InvalidOperationException("Carga fallida"));
        var cut = Renderizar();
        cut.FindAll(".tarjeta-listado-pie-separado").Should().BeEmpty();
        cut.FindComponents<PaginadorSimple>().Should().BeEmpty();
    }

    [Fact]
    public async Task La_carga_inicial_sin_filas_no_pinta_el_pie_ni_el_paginador()
    {
        var carga = new TaskCompletionSource<IReadOnlyList<ApplicationUser>>();
        _fuente.Visibles = _ => carga.Task;
        var cut = Renderizar();
        cut.FindComponents<EstadoCargando>().Should().ContainSingle();
        cut.FindAll(".tarjeta-listado-pie-separado").Should().BeEmpty();
        cut.FindComponents<PaginadorSimple>().Should().BeEmpty();
        await cut.InvokeAsync(() => carga.SetResult([]));
    }
}

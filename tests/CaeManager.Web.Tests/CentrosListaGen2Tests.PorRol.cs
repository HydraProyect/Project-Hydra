using Bunit;
using CaeManager.Application.Centros;
using CaeManager.Application.Centros.Queries.ObtenerCentros;
using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.Workspace;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// La lista de Centros 1:1 con la maqueta (filas en acordeón, sin Visita como columna, agrupar «Por Cliente»),
/// fijada por rol: lo que es lectura se comprueba para cada rol, incluido Consulta, y lo que solo es de escritura
/// aparece solo con escritura. La página no añade comandos de escritura nuevos: los que existen (alta, baja y
/// asignar a centros) ya los guarda AutorizacionEscrituraBehavior. El «Gestionar» de la fila desplegada vive en
/// el acordeón de asignaciones, que aquí es un stub: su regla por rol se fija con el acordeón real en
/// <see cref="CentrosEnlaceUnicoCentro360Tests"/>.
/// </summary>
public partial class CentrosListaGen2Tests
{
    private static readonly string[] RolesConEscritura = [Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae];
    private static readonly string[] RolesSoloLectura = [Roles.Consulta, Roles.Cliente];

    public static TheoryData<string> TodosLosRoles => Datos(RolesConEscritura.Concat(RolesSoloLectura));
    public static TheoryData<string> RolesDeEscritura => Datos(RolesConEscritura);
    public static TheoryData<string> RolesDeLectura => Datos(RolesSoloLectura);

    /// <summary>Cada rol con si puede escribir: lo que el resto de pruebas espera que cambie.</summary>
    public static TheoryData<string, bool> TodosLosRolesConEscritura
    {
        get
        {
            var datos = new TheoryData<string, bool>();
            foreach (var rol in RolesConEscritura) datos.Add(rol, true);
            foreach (var rol in RolesSoloLectura) datos.Add(rol, false);
            return datos;
        }
    }

    private static TheoryData<string> Datos(IEnumerable<string> roles)
    {
        var datos = new TheoryData<string>();
        foreach (var rol in roles) datos.Add(rol);
        return datos;
    }

    private static readonly Guid ClienteOrion = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid ClientePegaso = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    private static CentroListaDto CentroDeCliente(string nombre, Guid clienteId, string cliente, EstadoCentro estado = EstadoCentro.Vigente) =>
        Centro(nombre, estado) with { ClienteId = clienteId, ClienteRazonSocial = cliente };

    // --- «+ Nuevo centro»: la única acción primaria, solo con escritura ------------------------------

    [Theory]
    [MemberData(nameof(RolesDeEscritura))]
    public void Con_escritura_la_cabecera_ofrece_Nuevo_centro(string rol)
    {
        this.ConRolDeEscritura(rol);

        var cut = Renderizar(Centro("Centro Logístico Norte"));

        cut.Find("header.cabecera-pagina .cabecera-listado-primaria button").TextContent.Trim().Should().Be("+ Nuevo centro");
    }

    [Theory]
    [MemberData(nameof(RolesDeLectura))]
    public void Sin_escritura_la_cabecera_no_ofrece_Nuevo_centro(string rol)
    {
        this.ConRolDeEscritura(rol);

        var cut = Renderizar(Centro("Centro Logístico Norte"));

        cut.FindAll(".tarjeta-fila-acordeon").Should().ContainSingle("control positivo: la lista se ve");
        cut.FindAll("header.cabecera-pagina .cabecera-listado-primaria button").Should().BeEmpty();
        cut.Markup.Should().NotContain("+ Nuevo centro");
    }

    [Theory]
    [MemberData(nameof(TodosLosRolesConEscritura))]
    public void El_vacio_invita_a_crear_el_primero_solo_con_escritura(string rol, bool escribe)
    {
        this.ConRolDeEscritura(rol);

        var cut = Renderizar();

        cut.Find(".estado-vacio h3").TextContent.Trim().Should().Be("Aún no hay centros");
        cut.FindAll(".estado-vacio button").Select(b => b.TextContent.Trim()).Should().Equal(escribe ? ["+ Nuevo centro"] : []);
    }

    // --- Selección múltiple: marcar es lectura, la barra de lote es escritura -------------------------

    [Theory]
    [MemberData(nameof(TodosLosRolesConEscritura))]
    public async Task Todos_los_roles_marcan_filas_pero_la_barra_de_lote_solo_la_ve_quien_escribe(string rol, bool escribe)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(Centro("Centro Logístico Norte"), Centro("Centro Logístico Sur"));

        await cut.Find("header.cabecera-pagina button[aria-label='Selección múltiple']").ClickAsync(new MouseEventArgs());
        await cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Norte']").ChangeAsync(new ChangeEventArgs { Value = true });

        cut.Find("input[aria-label='Seleccionar el centro Centro Logístico Norte']").HasAttribute("checked").Should().BeTrue(
            "marcar una fila es lectura: cualquier rol puede");
        cut.FindAll(".cabecera-columnas-centros input[type=checkbox]").Should().ContainSingle("la casilla de «seleccionar los de esta página»");
        var barra = cut.FindAll(".barra-acciones-lote");
        barra.Count.Should().Be(escribe ? 1 : 0);
        cut.FindAll("button").Select(b => b.TextContent.Trim()).Contains("Eliminar seleccionados").Should().Be(escribe);
        cut.FindAll("button").Any(b => b.TextContent.Trim().StartsWith("Asignar a centros seleccionados", StringComparison.Ordinal))
            .Should().Be(escribe);
    }

    // --- Lo que es lectura se ve igual para todos ----------------------------------------------------

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void El_icono_360_es_un_enlace_a_la_ficha_del_centro_para_todos_los_roles(string rol)
    {
        this.ConRolDeEscritura(rol);
        var centro = Centro("Centro Logístico Norte");

        var cut = Renderizar(centro);

        var icono = cut.Find(".tarjeta-fila-acordeon-acciones a.boton-360-pagina");
        icono.GetAttribute("href").Should().Be($"/centros/{centro.Id}");
        icono.GetAttribute("aria-label").Should().Be("Abrir la ficha 360 de Centro Logístico Norte");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public async Task La_visita_enlaza_a_la_lista_de_visitas_filtrada_por_el_centro_para_todos_los_roles(string rol)
    {
        this.ConRolDeEscritura(rol);
        var centro = Centro("Centro Logístico Norte");
        _visitas[centro.Id] = [new VisitaResumenDto(Guid.NewGuid(), new DateOnly(2026, 10, 15), new DateOnly(2026, 10, 16), 3)];
        var cut = Renderizar(centro);
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        var visita = cut.Find(".celda-meta-centro .badge-visita");
        visita.TextContent.Trim().Should().Be("Visita 15/10–16/10", "la visita cierra la segunda línea del Centro, no es columna");
        await visita.ClickAsync(new MouseEventArgs());

        var navegacion = Services.GetRequiredService<NavigationManager>();
        new Uri(navegacion.Uri).PathAndQuery.Should().Be("/visitas?q=" + Uri.EscapeDataString(centro.Nombre));
        workspace.FrameActual.Should().BeNull("el clic de la visita no sube a la fila: no abre la vista rápida");
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public async Task El_codigo_se_copia_con_un_clic_sin_abrir_la_fila_para_todos_los_roles(string rol)
    {
        this.ConRolDeEscritura(rol);
        var cut = Renderizar(Centro("Centro Logístico Norte"));
        var workspace = Services.GetRequiredService<ContextWorkspaceService>();

        var codigo = cut.Find(".celda-identidad-centro .boton-copiar-en-linea");
        codigo.TextContent.Trim().Should().Be("C-001");
        codigo.GetAttribute("aria-label").Should().Be("Copiar el código C-001");
        await codigo.ClickAsync(new MouseEventArgs());

        workspace.FrameActual.Should().BeNull("copiar no abre la vista rápida");
    }

    // --- Agrupar «Por Cliente»: el título lleva el recuento -----------------------------------------

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Agrupada_cada_titulo_de_grupo_lleva_el_Cliente_y_su_recuento_para_todos_los_roles(string rol)
    {
        this.ConRolDeEscritura(rol);

        var cut = Renderizar("centros?agrupar=cliente",
            CentroDeCliente("Almacén Vigo", ClienteOrion, "Orion Cliente S.L.", EstadoCentro.Vencido),
            CentroDeCliente("Planta Murcia", ClienteOrion, "Orion Cliente S.L."),
            CentroDeCliente("Planta Bilbao", ClientePegaso, "Pegaso Cliente S.L."));

        var titulos = cut.FindAll(".cabecera-grupo-centros");
        titulos.Select(t => t.GetAttribute("data-grupo-cliente")).Should().Equal("Orion Cliente S.L.", "Pegaso Cliente S.L.");
        titulos.Select(t => t.QuerySelector(".cabecera-grupo-centros-recuento")!.TextContent.Trim()).Should().Equal("· 2", "· 1");
        cut.FindAll(".tarjeta-fila-acordeon").Should().HaveCount(3);
    }

    [Theory]
    [MemberData(nameof(TodosLosRoles))]
    public void Con_mas_de_una_pagina_el_recuento_del_grupo_dice_que_es_de_esta_pagina(string rol)
    {
        this.ConRolDeEscritura(rol);

        var cut = Renderizar("centros?agrupar=cliente",
            Enumerable.Range(1, 21).Select(i => CentroDeCliente($"Centro {i:00}", ClienteOrion, "Orion Cliente S.L.")).ToArray());

        cut.Find(".cabecera-grupo-centros .cabecera-grupo-centros-recuento").TextContent.Trim().Should().Be("· 20 en esta página");
    }

    [Fact]
    public void Sin_agrupar_la_lista_no_lleva_titulos_de_grupo()
    {
        var cut = Renderizar(
            CentroDeCliente("Almacén Vigo", ClienteOrion, "Orion Cliente S.L."),
            CentroDeCliente("Planta Bilbao", ClientePegaso, "Pegaso Cliente S.L."));

        cut.FindAll(".tarjeta-fila-acordeon").Should().HaveCount(2);
        cut.FindAll(".cabecera-grupo-centros").Should().BeEmpty();
    }
}

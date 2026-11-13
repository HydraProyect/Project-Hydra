using Bunit;
using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Documentos;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// D-18: menú «Acreditación ▾» de una fila del listado agrupado por Plataforma CAE. Fija (retro) lo que la pestaña Plataformas CAE
/// tenía en main: qué acciones ofrece cada estado de la acreditación y a qué rol se le enseñan, ahora en la regla y el componente
/// que comparten. Solo observa lo que se pinta y la acción que se avisa; la autorización de cada Command no se toca ni se mira aquí.
/// </summary>
public sealed class MenuAcreditacionTests : BunitContext
{
    private const string Portal = "https://nalanda.example.com";

    public MenuAcreditacionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private static readonly string[] Subir = ["Subir versión corregida"];
    private static readonly string[] Validar = ["Marcar validado"];
    private static readonly string[] Rechazar = ["Marcar rechazado…"];
    private static readonly string[] Marcar = ["Marcar subido"];
    private static readonly string[] Anotar = ["Anotar vigencia…"];
    private const string Gestionar = "Gestionar acreditación…";
    private const string AbrirPortal = "Abrir portal ↗";

    private async Task<IRenderedComponent<MenuAcreditacion>> AbrirAsync(string rol, EstadoAcreditacion estado, string? urlPortal = Portal,
        bool puedeGestionar = true, Action<AccionAcreditacion>? alElegir = null)
    {
        this.ConRolDeEscritura(rol);
        var cut = Render<MenuAcreditacion>(p => p
            .Add(c => c.Estado, estado)
            .Add(c => c.PlataformaNombre, "Nalanda")
            .Add(c => c.PropietarioNombre, "Lucía Ferrer")
            .Add(c => c.UrlPortal, urlPortal)
            .Add(c => c.PuedeGestionar, puedeGestionar)
            .Add(c => c.OnAccion, EventCallback.Factory.Create<AccionAcreditacion>(this, a => alElegir?.Invoke(a))));
        if (cut.FindAll("button.menu-acciones-disparador").Count > 0)
            await cut.Find("button.menu-acciones-disparador").ClickAsync(new MouseEventArgs());
        return cut;
    }

    private static List<string> Opciones(IRenderedComponent<MenuAcreditacion> cut) =>
        cut.FindAll("[role=menuitem]").Select(o => o.TextContent.Trim()).ToList();

    // ------------------------------------------------------------- qué ofrece cada estado (lo de PlataformaTab en main)

    [Fact]
    public async Task Una_pendiente_de_subir_ofrece_marcar_subido_validado_rechazado_gestionar_y_portal()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.PendienteDeSubir);

        Opciones(cut).Should().Equal(Marcar.Concat(Validar).Concat(Rechazar).Append(Gestionar).Append(AbrirPortal));
    }

    [Fact]
    public async Task Una_subida_ofrece_validado_y_rechazado_pero_no_marcar_subido_ni_portal()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.Subida);

        Opciones(cut).Should().Equal(Validar.Concat(Rechazar).Append(Gestionar));
    }

    [Fact]
    public async Task Una_rechazada_ofrece_subir_la_corregida_validado_rechazado_gestionar_y_portal()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.Rechazada);

        Opciones(cut).Should().Equal(Subir.Concat(Validar).Concat(Rechazar).Append(Gestionar).Append(AbrirPortal));
    }

    [Fact]
    public async Task Una_validada_solo_ofrece_anotar_vigencia_y_gestionar()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.Aceptada);

        Opciones(cut).Should().Equal(Anotar.Append(Gestionar));
    }

    [Fact]
    public async Task Una_no_requerida_solo_ofrece_gestionar()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.NoRequerida);

        Opciones(cut).Should().Equal(Gestionar);
    }

    [Fact]
    public async Task Sin_ficha_a_la_que_enlazar_no_se_ofrece_gestionar_ni_el_separador()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.Aceptada, puedeGestionar: false);

        Opciones(cut).Should().Equal(Anotar);
        cut.FindAll("hr.menu-acreditacion-separador").Should().BeEmpty();
    }

    // ------------------------------------------------------------- «Abrir portal»

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ftp://portal.example.com")]
    public async Task Sin_una_direccion_http_valida_no_hay_abrir_portal(string? url)
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.Rechazada, url);

        Opciones(cut).Should().NotContain(AbrirPortal);
        cut.FindAll("a[role=menuitem]").Should().BeEmpty();
    }

    [Fact]
    public async Task Abrir_portal_es_un_enlace_a_pestana_nueva_sin_acceso_a_la_pagina_de_origen()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.PendienteDeSubir);

        var enlace = cut.Find("a[role=menuitem]");
        enlace.GetAttribute("href").Should().StartWith("https://nalanda.example.com");
        enlace.GetAttribute("target").Should().Be("_blank");
        enlace.GetAttribute("rel").Should().Be("noopener noreferrer");
    }

    // ------------------------------------------------------------- por rol (D-3): solo cambia qué se muestra

    [Theory]
    [InlineData(Roles.Administrador)]
    [InlineData(Roles.DireccionCae)]
    [InlineData(Roles.CoordinadorCae)]
    [InlineData(Roles.GestorCae)]
    public async Task Los_roles_con_escritura_ven_el_menu_y_el_portal(string rol)
    {
        var cut = await AbrirAsync(rol, EstadoAcreditacion.Rechazada);

        cut.Find("button.menu-acciones-disparador").TextContent.Trim().Should().Be("Acreditación");
        Opciones(cut).Should().Contain(AbrirPortal).And.Contain(Gestionar);
    }

    [Theory]
    [InlineData(Roles.Consulta)]
    [InlineData(Roles.Cliente)]
    public async Task Consulta_y_Usuario_de_Cliente_no_ven_el_menu(string rol)
    {
        var cut = await AbrirAsync(rol, EstadoAcreditacion.Rechazada);

        cut.FindAll("button.menu-acciones-disparador").Should().BeEmpty();
        cut.FindAll(".menu-acreditacion").Should().BeEmpty();
        cut.Markup.Should().NotContain(AbrirPortal).And.NotContain(Gestionar);
    }

    // ------------------------------------------------------------- aspecto y aviso

    [Fact]
    public async Task El_panel_es_el_titulado_compacto_de_32_px_y_el_boton_tiene_nombre_accesible_por_fila()
    {
        var cut = await AbrirAsync(Roles.GestorCae, EstadoAcreditacion.Subida);

        var panel = cut.Find("[role=menu]");
        panel.ClassList.Should().Contain("menu-acciones-panel-compacto").And.Contain("menu-acciones-panel-titulado");
        panel.QuerySelector(".menu-acciones-titulo")!.TextContent.Trim().Should().Be("Acreditación en Nalanda");
        cut.Find("button.menu-acciones-disparador").GetAttribute("aria-label").Should().Be("Acreditación en Nalanda de Lucía Ferrer");
        cut.Find("button.menu-acciones-disparador").GetAttribute("aria-haspopup").Should().Be("menu");
    }

    [Theory]
    [InlineData("Subir versión corregida", AccionAcreditacion.SubirVersionCorregida, EstadoAcreditacion.Rechazada)]
    [InlineData("Marcar subido", AccionAcreditacion.MarcarSubido, EstadoAcreditacion.PendienteDeSubir)]
    [InlineData("Marcar validado", AccionAcreditacion.MarcarValidado, EstadoAcreditacion.Subida)]
    [InlineData("Marcar rechazado…", AccionAcreditacion.MarcarRechazado, EstadoAcreditacion.Subida)]
    [InlineData("Anotar vigencia…", AccionAcreditacion.AnotarVigencia, EstadoAcreditacion.Aceptada)]
    [InlineData("Gestionar acreditación…", AccionAcreditacion.Gestionar, EstadoAcreditacion.Subida)]
    public async Task Cada_opcion_avisa_de_su_accion_y_cierra_el_menu(string texto, AccionAcreditacion esperada, EstadoAcreditacion estado)
    {
        var elegidas = new List<AccionAcreditacion>();
        var cut = await AbrirAsync(Roles.GestorCae, estado, alElegir: elegidas.Add);

        await cut.FindAll("[role=menuitem]").Single(o => o.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

        elegidas.Should().Equal(esperada);
        cut.FindAll("[role=menu]").Should().BeEmpty("elegir una opción cierra el menú");
    }
}

/// <summary>
/// Las reglas de estado que comparten la pestaña Plataformas CAE y el menú: una tabla por estado, tal como las tenía main en
/// PlataformaTab (solo «Subir versión corregida» en rechazadas, «Marcar subido» en pendientes, «Anotar vigencia» en validadas).
/// </summary>
public sealed class ReglasAccionesAcreditacionTests
{
    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, false, true, true, true, false)]
    [InlineData(EstadoAcreditacion.Subida, false, false, true, true, false)]
    [InlineData(EstadoAcreditacion.Rechazada, true, false, true, true, false)]
    [InlineData(EstadoAcreditacion.Aceptada, false, false, false, false, true)]
    [InlineData(EstadoAcreditacion.NoRequerida, false, false, false, false, false)]
    public void Cada_estado_ofrece_solo_las_transiciones_que_permite(EstadoAcreditacion estado,
        bool subirCorregida, bool marcarSubido, bool validado, bool rechazado, bool anotarVigencia)
    {
        ReglasAccionesAcreditacion.PuedeSubirVersionCorregida(estado).Should().Be(subirCorregida);
        ReglasAccionesAcreditacion.PuedeMarcarSubido(estado).Should().Be(marcarSubido);
        ReglasAccionesAcreditacion.PuedeMarcarValidado(estado).Should().Be(validado);
        ReglasAccionesAcreditacion.PuedeMarcarRechazado(estado).Should().Be(rechazado);
        ReglasAccionesAcreditacion.PuedeAnotarVigencia(estado).Should().Be(anotarVigencia);
    }

    [Theory]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, true)]
    [InlineData(EstadoAcreditacion.Rechazada, true)]
    [InlineData(EstadoAcreditacion.Subida, false)]
    [InlineData(EstadoAcreditacion.Aceptada, false)]
    [InlineData(EstadoAcreditacion.NoRequerida, false)]
    public void El_portal_solo_se_ofrece_en_pendiente_o_rechazada(EstadoAcreditacion estado, bool ofrece)
    {
        (ReglasAccionesAcreditacion.DireccionDelPortal(estado, "https://nalanda.example.com") is not null).Should().Be(ofrece);
    }

    [Theory]
    [InlineData(EstadoAcreditacion.NoRequerida, false, null, false)]
    [InlineData(EstadoAcreditacion.NoRequerida, true, null, true)]
    [InlineData(EstadoAcreditacion.NoRequerida, false, "https://nalanda.example.com", false)]
    [InlineData(EstadoAcreditacion.Subida, false, null, true)]
    [InlineData(EstadoAcreditacion.Aceptada, false, null, true)]
    [InlineData(EstadoAcreditacion.PendienteDeSubir, false, null, true)]
    public void El_menu_solo_se_pinta_si_tiene_alguna_opcion(EstadoAcreditacion estado, bool puedeGestionar, string? url, bool hayAlguna)
    {
        ReglasAccionesAcreditacion.HayAlgunaAccion(estado, puedeGestionar, url).Should().Be(hayAlguna);
    }

    [Fact]
    public void Los_roles_que_abren_el_portal_son_los_de_escritura_y_Consulta_nunca_el_Usuario_de_Cliente()
    {
        var roles = ReglasAccionesAcreditacion.RolesQueAbrenPortal.Split(',');

        roles.Should().BeEquivalentTo(Roles.Administrador, Roles.DireccionCae, Roles.CoordinadorCae, Roles.GestorCae, Roles.Consulta);
        roles.Should().NotContain(Roles.Cliente);
    }
}

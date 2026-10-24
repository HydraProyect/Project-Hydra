using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Usuarios.Commands.GenerarActivacionUsuario;
using FluentAssertions;
using RolesIdentidad = CaeManager.Infrastructure.Identity.Roles;

namespace CaeManager.Web.Tests;

/// <summary>
/// Maqueta «Flujos 14 Usuarios», solo lo que no pedía una decisión: el pie del alta («Crear y enviar enlace», deshabilitado
/// con su motivo mientras el formulario está vacío), el modal del enlace («Cerrar» y «Copiar enlace»), y la ficha de edición
/// con el estado, el último acceso y la 2FA en la cabecera y «Generar enlace nuevo» si la cuenta sigue pendiente.
/// </summary>
public partial class UsuariosGen2Tests
{
    private static IElement PrimarioDelPie(IRenderedComponent<UsuariosControlados> cut) => cut.Find(".drawer-pie .boton-primario");

    [Fact]
    public async Task El_alta_termina_en_Crear_y_enviar_enlace_y_esta_deshabilitado_con_su_motivo_mientras_esta_vacio()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));

        var cut = Renderizar();
        await cut.Find(".cabecera-listado-primaria button").ClickAsync(new());

        var vacio = PrimarioDelPie(cut);
        vacio.TextContent.Trim().Should().Be("Crear y enviar enlace");
        vacio.HasAttribute("disabled").Should().BeTrue("sin nombre ni correo no hay a quién enviar el enlace");
        vacio.GetAttribute("title").Should().Be("Escribe el nombre y el correo para crear el usuario.", "un primario mudo no existe (D-02, D-06)");

        await EscribirAsync(cut, "Correo", "nuria.sanz@talveg.es");
        PrimarioDelPie(cut).HasAttribute("disabled").Should().BeTrue("con solo el correo sigue faltando el nombre");

        await EscribirAsync(cut, "Nombre completo", "Nuria Sanz");
        var listo = PrimarioDelPie(cut);
        listo.HasAttribute("disabled").Should().BeFalse();
        listo.HasAttribute("title").Should().BeFalse("habilitado no lleva motivo");
    }

    [Fact]
    public async Task El_pie_de_la_edicion_sigue_siendo_Guardar_y_no_pide_nombre_ni_correo_nuevos()
    {
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia"), RolesIdentidad.GestorCae));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        cut.WaitForAssertion(() =>
        {
            PrimarioDelPie(cut).TextContent.Trim().Should().Be("Guardar");
            PrimarioDelPie(cut).HasAttribute("disabled").Should().BeFalse();
        });
    }

    [Fact]
    public async Task El_modal_del_enlace_tras_el_alta_ofrece_Cerrar_y_Copiar_enlace_y_el_boton_confirma_Copiado()
    {
        Sembrar((Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador));
        var clipboard = JSInterop.SetupModule("./js/clipboard.js");
        clipboard.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();

        var cut = Renderizar();
        await cut.Find(".cabecera-listado-primaria button").ClickAsync(new());
        await EscribirAsync(cut, "Correo", "nuria.sanz@talveg.es");
        await EscribirAsync(cut, "Nombre completo", "Nuria Sanz");
        await GuardarAsync(cut);

        cut.WaitForAssertion(() => cut.Find(".enlace-activacion").TextContent.Should().Contain("/cuenta/restablecer-contrasena"));
        var botones = cut.FindAll("[role=dialog] .modal-pie button");
        botones.Select(b => b.TextContent.Trim()).Should().Equal("Cerrar", "Copiar enlace");
        botones[0].ClassList.Should().Contain("boton-secundario");
        botones[1].ClassList.Should().Contain("boton-primario");
        cut.Find(".modal-header h2").TextContent.Trim().Should().Be("Enlace de activación");

        await cut.FindAll("[role=dialog] .modal-pie button")[1].ClickAsync(new());

        cut.WaitForAssertion(() =>
            cut.FindAll("[role=dialog] .modal-pie button")[1].TextContent.Trim().Should().Be("✓ Copiado"));
        clipboard.VerifyInvoke("copiarAlPortapapeles");
        _toasts.Mensajes.Should().Contain(m => m.Mensaje == "Enlace copiado al portapapeles.");

        await cut.FindAll("[role=dialog] .modal-pie button")[0].ClickAsync(new());
        cut.WaitForAssertion(() => cut.FindAll(".enlace-activacion").Should().BeEmpty("«Cerrar» cierra el modal"));
    }

    [Fact]
    public async Task La_ficha_de_edicion_enseña_estado_ultimo_acceso_y_2FA_en_la_cabecera()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia");
        ander.UltimaActividadUtc = DateTime.UtcNow;
        ander.TwoFactorEnabled = true;
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.GestorCae));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        cut.WaitForAssertion(() => cut.Find(".drawer-titulos .drawer-subtitulo").TextContent.Trim().Should().Be("Ander Beitia"));
        cut.Find(".drawer-titulos [data-pieza=pastilla]").TextContent.Trim().Should().Be("Activo");
        var datos = cut.Find(".drawer-cuerpo .rejilla-datos").TextContent;
        datos.Should().Contain("Último acceso").And.Contain("Hoy ");
        datos.Should().Contain("2FA").And.Contain("Activado");
        cut.FindAll(".drawer-cuerpo .aviso-pendiente-activacion").Should().BeEmpty("una cuenta activa no ofrece enlace nuevo");
    }

    [Fact]
    public async Task Una_cuenta_que_nunca_ha_entrado_y_sin_2FA_lo_dice()
    {
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia", activa: false), RolesIdentidad.GestorCae));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        cut.WaitForAssertion(() => cut.Find(".drawer-titulos [data-pieza=pastilla]").TextContent.Trim().Should().Be("Desactivado"));
        var datos = cut.Find(".drawer-cuerpo .rejilla-datos").TextContent;
        datos.Should().Contain("Aún no ha entrado").And.Contain("No activado");
    }

    [Fact]
    public async Task Generar_enlace_nuevo_desde_la_ficha_de_una_cuenta_pendiente_usa_el_comando_existente_y_no_envia_correo()
    {
        var ander = Cuenta(AnderId, "a.beitia@talveg.es", "Ander Beitia", pendienteActivacion: true);
        Sembrar(
            (Cuenta(MartaId, "marta.r@talveg.es", "Marta Rodríguez"), RolesIdentidad.Administrador),
            (ander, RolesIdentidad.GestorCae));

        var cut = Renderizar(actorId: MartaId);
        await PulsarEnMenuAsync(cut, "a.beitia@talveg.es", "Editar");

        cut.WaitForAssertion(() => cut.Find(".drawer-titulos [data-pieza=pastilla]").TextContent.Trim().Should().Be("Pendiente de activar"));
        var aviso = cut.Find(".drawer-cuerpo .aviso-pendiente-activacion");
        aviso.TextContent.Should().Contain("Todavía no ha activado la cuenta");

        await aviso.QuerySelector("button")!.ClickAsync(new());

        cut.WaitForAssertion(() => cut.Find(".enlace-activacion").TextContent.Should().Contain("/cuenta/restablecer-contrasena"));
        _mediador.Enviadas.OfType<GenerarActivacionUsuarioCommand>().Should().ContainSingle().Which.UsuarioId.Should().Be(AnderId);
        _correo.Enviados.Should().BeEmpty("generar el enlace no envía correo: se entrega por otra vía");
        cut.Find(".modal-header h2").TextContent.Trim().Should().Be("Enlace de activación");
        cut.Find("[role=dialog] .modal-cuerpo").TextContent
            .Should().Contain("El enlace anterior deja de valer.").And.Contain("Solo funciona el último que generes.");
        cut.FindAll("[role=dialog] .modal-pie button").Select(b => b.TextContent.Trim()).Should().Equal("Cerrar", "Copiar enlace");
    }
}

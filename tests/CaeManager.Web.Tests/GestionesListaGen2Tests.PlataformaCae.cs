using Bunit;
using CaeManager.Application.Centros.Queries.ObtenerCanalesGestionDeCentro;
using CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;
using CaeManager.Domain.Centros;
using CaeManager.Application.Gestiones.Queries.ObtenerGestiones;
using CaeManager.Domain.Gestiones;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Gestiones.Pages;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Bloque «Plataforma CAE» de la vista rápida de una Gestión: en qué Plataforma CAE hay que hacerla. La Gestión no
/// guarda plataforma; se lee de los canales de gestión de su Centro (los de Centro 360) y solo los de tipo Plataforma.
/// «Abrir portal» y «Copiar usuario / contraseña» son el componente de Centro 360 (<c>CanalPlataformaAcceso</c>), cuyo
/// comportamiento (consulta con alcance de gestión, rol con escritura, segundo factor) prueba <c>Centro360Gen2Tests</c>;
/// aquí se prueba que esta pantalla lo monta con el Centro de la Gestión y que no abre otra vía a un secreto.
/// </summary>
public partial class GestionesListaGen2Tests
{
    private static CanalGestionResumenDto CanalDePlataforma(string? url, string proveedor = "Nalanda", bool principal = true) =>
        new(Guid.NewGuid(), TipoCanalGestion.Plataforma, "Gestión general", principal, Guid.NewGuid(), proveedor,
            url, null, null, null, true, Guid.NewGuid());

    private static CanalGestionResumenDto CanalDeCorreo() =>
        new(Guid.NewGuid(), TipoCanalGestion.Email, "Aviso por correo", false, null, null,
            null, "gestion@ejemplo.test", null, null, true, Guid.NewGuid());

    private async Task<(IRenderedComponent<Gestiones> Cut, MediatorFalso Mediador, GestionListaDto Gestion)> VistaRapidaConCanales(
        params CanalGestionResumenDto[] canales)
    {
        var gestion = Gestion("Nuria Salas Ortiz", centro: "Centro Logístico Sur", documento: "Reconocimiento médico");
        var mediador = new MediatorFalso { Almacen = { gestion } };
        mediador.Canales[gestion.CentroId] = canales;
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.FindAll("aside.vista-rapida-gestion [data-plataforma-cae]").Should().ContainSingle());
        cut.WaitForAssertion(() => cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]").TextContent.Should().NotContain("Cargando"));
        return (cut, mediador, gestion);
    }

    private static IEnumerable<string> BotonesDeCopiar(IRenderedComponent<Gestiones> cut) =>
        cut.FindAll("aside.vista-rapida-gestion [data-plataforma-cae] .boton-copiar").Select(b => b.TextContent.Trim());

    [Fact]
    public async Task La_vista_rapida_dice_en_que_Plataforma_CAE_hay_que_hacer_la_Gestion_y_de_que_Centro()
    {
        var (cut, mediador, gestion) = await VistaRapidaConCanales(CanalDePlataforma("app.nalanda.test/login"));

        var bloque = cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]");
        bloque.TextContent.Should().Contain("Plataforma CAE").And.Contain("Nalanda").And.Contain("Principal")
            .And.Contain("Centro: Centro Logístico Sur");
        mediador.Enviadas.OfType<ObtenerCanalesGestionDeCentroQuery>().Should().ContainSingle()
            .Which.CentroId.Should().Be(gestion.CentroId, "se piden los canales del Centro de ESA Gestión");

        var enlace = bloque.QuerySelectorAll("a").Should().ContainSingle(a => a.TextContent.Contains("Abrir portal")).Subject;
        enlace.GetAttribute("href").Should().Be("https://app.nalanda.test/login");
        enlace.GetAttribute("target").Should().Be("_blank");
        enlace.GetAttribute("rel").Should().Contain("noopener").And.Contain("noreferrer");
    }

    [Fact]
    public async Task Solo_se_muestran_los_canales_de_tipo_Plataforma_no_los_de_correo()
    {
        var (cut, _, _) = await VistaRapidaConCanales(CanalDeCorreo(), CanalDePlataforma("app.nalanda.test", "Nalanda"),
            CanalDePlataforma("portal.twind.test", "Twind", principal: false));

        cut.FindAll("aside.vista-rapida-gestion [data-plataforma-canal]")
            .Select(c => c.QuerySelector(".plataforma-nombre-vista-rapida-gestion")!.TextContent.Replace("Principal", "").Trim())
            .Should().Equal(["Nalanda", "Twind"], "un Centro puede tener varias plataformas y el correo no es una");
        cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]").TextContent.Should().NotContain("gestion@ejemplo.test");
    }

    [Fact]
    public async Task Un_Centro_sin_canal_de_plataforma_lo_dice_y_no_ofrece_nada()
    {
        var (cut, _, _) = await VistaRapidaConCanales(CanalDeCorreo());

        cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]").TextContent
            .Should().Contain("Los canales de este Centro no incluyen ninguna Plataforma CAE");
        cut.FindAll("aside.vista-rapida-gestion [data-plataforma-cae] a").Should().BeEmpty();
        BotonesDeCopiar(cut).Should().BeEmpty();
    }

    [Fact]
    public async Task Si_los_canales_no_cargan_el_bloque_lo_dice_y_el_resto_de_la_vista_rapida_sigue_funcionando()
    {
        var gestion = Gestion("Nuria Salas Ortiz", centro: "Centro Logístico Sur", documento: "Reconocimiento médico");
        var mediador = new MediatorFalso { Almacen = { gestion }, FalloCanales = new InvalidOperationException("boom") };
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]").TextContent
            .Should().Contain("No se pudo cargar la Plataforma CAE"));
        cut.Find("aside.vista-rapida-gestion .nombre-vista-rapida-gestion").TextContent.Trim().Should().Be("Nuria Salas Ortiz");
        BotonDeLaVistaRapida(cut, "Marcar completada").Should().NotBeNull();
    }

    /// <summary>
    /// Abrir la vista rápida NO pide ni pinta la credencial (DEC-53/DEC-62): el valor solo se pide en el clic, y el DTO
    /// de los canales no lo lleva.
    /// </summary>
    [Fact]
    public async Task Abrir_la_vista_rapida_no_pide_la_credencial_ni_la_pinta()
    {
        var canal = CanalDePlataforma("app.nalanda.test");
        var gestion = Gestion("Nuria Salas Ortiz");
        var mediador = new MediatorFalso { Almacen = { gestion } };
        mediador.Canales[gestion.CentroId] = [canal];
        mediador.Credenciales[(gestion.CentroId, canal.Id)] = new("usuario.secreto", "clave-secreta-123");
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => BotonesDeCopiar(cut).Should().NotBeEmpty());

        mediador.Enviadas.OfType<ObtenerCredencialCanalGestionQuery>().Should().BeEmpty();
        cut.Markup.Should().NotContain("usuario.secreto").And.NotContain("clave-secreta-123");
    }

    [Fact]
    public async Task Un_canal_de_plataforma_sin_credenciales_no_ofrece_copiar_nada()
    {
        var (cut, _, _) = await VistaRapidaConCanales(CanalDePlataforma("app.nalanda.test") with { TieneCredenciales = false });

        BotonesDeCopiar(cut).Should().BeEmpty("sin credenciales no hay nada que copiar");
    }

    [Theory]
    [InlineData("Consulta", false)]
    [InlineData("GestorCae", true)]
    public async Task Copiar_usuario_y_contrasena_solo_se_ofrece_a_los_roles_con_escritura(string rol, bool seOfrece)
    {
        this.ConRolDeEscritura(rol);
        var (cut, _, _) = await VistaRapidaConCanales(CanalDePlataforma("app.nalanda.test"));

        if (seOfrece)
            BotonesDeCopiar(cut).Should().Contain(["Copiar usuario", "Copiar contraseña"]);
        else
            BotonesDeCopiar(cut).Should().NotContain(["Copiar usuario", "Copiar contraseña"]);
    }

    [Theory]
    [InlineData("Copiar usuario", "usuario.secreto")]
    [InlineData("Copiar contraseña", "clave-secreta-123")]
    public async Task El_clic_pide_la_credencial_del_canal_y_del_Centro_de_la_Gestion_y_la_manda_al_portapapeles(string boton, string esperado)
    {
        var canal = CanalDePlataforma("app.nalanda.test");
        var gestion = Gestion("Nuria Salas Ortiz");
        var mediador = new MediatorFalso { Almacen = { gestion } };
        mediador.Canales[gestion.CentroId] = [canal];
        mediador.Credenciales[(gestion.CentroId, canal.Id)] = new("usuario.secreto", "clave-secreta-123");
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar(mediador);
        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => BotonesDeCopiar(cut).Should().NotBeEmpty());

        await cut.FindAll("aside.vista-rapida-gestion [data-plataforma-cae] .boton-copiar").Single(b => b.TextContent.Trim() == boton)
            .ClickAsync(new MouseEventArgs());

        mediador.Enviadas.OfType<ObtenerCredencialCanalGestionQuery>().Should().ContainSingle()
            .Which.Should().Be(new ObtenerCredencialCanalGestionQuery(gestion.CentroId, canal.Id));
        modulo.VerifyInvoke("copiarAlPortapapeles").Arguments.Should().Equal(esperado);
        cut.Markup.Should().NotContain(esperado, "el valor va al portapapeles, no a la pantalla");
    }

    [Theory]
    [InlineData("Copiar usuario")]
    [InlineData("Copiar contraseña")]
    public async Task Sin_2FA_el_clic_lleva_a_configurar_el_2FA_y_no_copia(string boton)
    {
        var canal = CanalDePlataforma("app.nalanda.test");
        var gestion = Gestion("Nuria Salas Ortiz");
        var mediador = new MediatorFalso { Almacen = { gestion }, SinDobleFactor = true };
        mediador.Canales[gestion.CentroId] = [canal];
        mediador.Credenciales[(gestion.CentroId, canal.Id)] = new("usuario.secreto", "clave-secreta-123");
        var modulo = JSInterop.SetupModule("./js/clipboard.js");
        modulo.SetupVoid("copiarAlPortapapeles", _ => true).SetVoidResult();
        var cut = Renderizar(mediador);
        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => BotonesDeCopiar(cut).Should().NotBeEmpty());

        await cut.FindAll("aside.vista-rapida-gestion [data-plataforma-cae] .boton-copiar").Single(b => b.TextContent.Trim() == boton)
            .ClickAsync(new MouseEventArgs());

        Services.GetRequiredService<NavigationManager>().Uri.Should().Contain("/cuenta/configurar-2fa?motivo=credenciales");
        modulo.Invocations.Should().BeEmpty("sin 2FA no hay nada que copiar");
    }

    [Fact]
    public async Task Cambiar_a_la_Gestion_de_otro_Centro_recarga_las_plataformas_de_ese_Centro()
    {
        var norte = Gestion("Juan Pérez Ibarra", centro: "Centro Norte");
        var sur = Gestion("Nuria Salas Ortiz", centro: "Centro Logístico Sur");
        var mediador = new MediatorFalso { Almacen = { norte, sur } };
        mediador.Canales[norte.CentroId] = [CanalDePlataforma("norte.test", "Nalanda")];
        mediador.Canales[sur.CentroId] = [CanalDePlataforma("sur.test", "Twind")];
        var cut = Renderizar(mediador);

        await NombreEnLaFila(cut, "Juan Pérez Ibarra").ClickAsync(new MouseEventArgs());
        cut.WaitForAssertion(() => cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]").TextContent.Should().Contain("Nalanda"));
        await NombreEnLaFila(cut, "Nuria Salas Ortiz").ClickAsync(new MouseEventArgs());

        cut.WaitForAssertion(() => cut.Find("aside.vista-rapida-gestion [data-plataforma-cae]").TextContent
            .Should().Contain("Twind").And.NotContain("Nalanda"));
        mediador.Enviadas.OfType<ObtenerCanalesGestionDeCentroQuery>().Select(q => q.CentroId)
            .Should().Equal(norte.CentroId, sur.CentroId);
    }
}

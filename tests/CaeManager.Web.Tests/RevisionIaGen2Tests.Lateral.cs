using AngleSharp.Dom;
using Bunit;
using CaeManager.Application.Documentos.Queries.ObtenerRevisionesIaPendientes;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// Revisión IA con la revisión en lateral (D-49, artboard «Revision-ia»): selector con filtros, «1 de N ‹ ›», avance
/// automático recordado por usuario, atajos J/K, Consulta sin acciones y estados vacíos.
/// </summary>
public partial class RevisionIaGen2Tests
{
    private static readonly DateOnly Vence = new(2027, 3, 1);

    private static string Selector(IRenderedComponent<RevisionIaTab> cut) => cut.Find(".revision-ia-selector-n").TextContent.Trim();

    private static async Task AbrirDesplegableAsync(IRenderedComponent<RevisionIaTab> cut)
    {
        if (cut.FindAll(".revision-ia-pop").Count == 0)
        {
            await cut.Find(".revision-ia-selector-boton").ClickAsync(new MouseEventArgs());
        }
    }

    private static async Task SeleccionarEnDesplegableAsync(IRenderedComponent<RevisionIaTab> cut, string propietario)
    {
        await AbrirDesplegableAsync(cut);
        var opcion = cut.FindAll("[role=option]")
            .Should().ContainSingle(o => o.TextContent.Contains(propietario), $"la cola ofrece «{propietario}»").Subject;
        await opcion.ClickAsync(new MouseEventArgs());
    }

    private static IElement Navegar(IRenderedComponent<RevisionIaTab> cut, string etiqueta) => cut.Find($"button[aria-label='{etiqueta}']");

    private static MediadorFalso TresQueSeResuelven()
    {
        var mediador = new MediadorFalso
        {
            Revisiones = [Revision("Primera", 50, Vence), Revision("Segunda", 60, Vence), Revision("Tercera", 70, Vence)]
        };
        mediador.AlAplicar = c =>
        {
            mediador.Revisiones = mediador.Revisiones.Where(r => r.Id != c.RevisionId).ToList();
            return Result.Exito();
        };
        return mediador;
    }

    [Fact]
    public async Task Con_el_avance_automatico_activado_se_pasa_solo_a_la_siguiente_con_aviso()
    {
        var mediador = TresQueSeResuelven();
        var cut = Renderizar(mediador);
        cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeTrue("D-49: el avance automático viene activado");

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        var aviso = Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle().Subject;
        aviso.Mensaje.Should().Be("Primera · Formación PRL revisado. Pasamos a Segunda.");
        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Segunda", "la revisión elegida pasa a ser la siguiente de la cola");
        Selector(cut).Should().Be("1 de 2");
    }

    [Fact]
    public async Task Con_el_avance_automatico_desactivado_no_se_elige_otra_y_se_recuerda_la_preferencia()
    {
        var usuarioId = Guid.NewGuid();
        var modulo = JSInterop.SetupModule("./js/revision-ia.js");
        modulo.Setup<string?>("leer", _ => true).SetResult(null);
        modulo.SetupVoid("guardar", _ => true);
        var mediador = TresQueSeResuelven();
        Registrar(mediador);
        this.ConRolDeEscritura(usuarioId: usuarioId);
        var cut = Render<RevisionIaTab>();

        await cut.Find(".revision-ia-avance input").ChangeAsync(new ChangeEventArgs { Value = false });
        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        var guardado = modulo.Invocations["guardar"].Should().ContainSingle("la preferencia se guarda al cambiarla").Subject;
        guardado.Arguments.Should().Equal($"revision-ia:avance-automatico:{usuarioId}", "0");
        Services.GetRequiredService<ToastService>().Mensajes.Should().ContainSingle(m => !m.Mensaje.Contains("Pasamos a"));
        cut.FindAll(".revision-ia-cab").Should().BeEmpty("con el avance apagado el lateral pide elegir del desplegable");
        cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeFalse();
    }

    [Fact]
    public void La_preferencia_guardada_del_usuario_se_lee_al_abrir_la_pantalla()
    {
        var usuarioId = Guid.NewGuid();
        var modulo = JSInterop.SetupModule("./js/revision-ia.js");
        modulo.Setup<string?>("leer", _ => true).SetResult("0");
        Registrar(TresQueSeResuelven());
        this.ConRolDeEscritura(usuarioId: usuarioId);

        var cut = Render<RevisionIaTab>();

        cut.WaitForAssertion(() => cut.Find(".revision-ia-avance input").HasAttribute("checked").Should().BeFalse("el usuario lo había desactivado"));
        modulo.Invocations["leer"].Should().ContainSingle().Which.Arguments.Should().Equal($"revision-ia:avance-automatico:{usuarioId}");
    }

    [Fact]
    public async Task La_posicion_y_las_flechas_siguen_a_la_lista_filtrada()
    {
        var sinFecha = Revision("SinFecha", 40, null);
        var mediador = new MediadorFalso { Revisiones = [sinFecha, Revision("Media", 70, Vence), Revision("Alta", 97, Vence)] };
        var cut = Renderizar(mediador);

        Selector(cut).Should().Be("1 de 3");
        Navegar(cut, "Anterior (K)").HasAttribute("disabled").Should().BeTrue("la primera no tiene anterior");
        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        Selector(cut).Should().Be("2 de 3");
        await Navegar(cut, "Siguiente (J)").ClickAsync(new MouseEventArgs());
        Selector(cut).Should().Be("3 de 3");
        Navegar(cut, "Siguiente (J)").HasAttribute("disabled").Should().BeTrue("la última no tiene siguiente");

        await AbrirDesplegableAsync(cut);
        await cut.Find("[data-filtro=sinfecha]").ClickAsync(new MouseEventArgs());

        Selector(cut).Should().Be("1 de 1", "«n de m» cuenta solo lo que deja ver el filtro");
        Navegar(cut, "Anterior (K)").HasAttribute("disabled").Should().BeTrue();
        Navegar(cut, "Siguiente (J)").HasAttribute("disabled").Should().BeTrue();
        await AbrirDesplegableAsync(cut);
        cut.FindAll("[role=option]").Should().ContainSingle(o => o.TextContent.Contains("SinFecha"));
    }

    [Fact]
    public async Task Los_atajos_j_y_k_recorren_la_cola()
    {
        var cut = Renderizar(TresQueSeResuelven());
        var atajos = cut.FindComponent<AtajosListaTeclado>().Instance;

        await cut.InvokeAsync(() => atajos.RecibirAtajo("j"));
        Selector(cut).Should().Be("2 de 3");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("k"));
        Selector(cut).Should().Be("1 de 3");
        await cut.InvokeAsync(() => atajos.RecibirAtajo("x"));
        Selector(cut).Should().Be("1 de 3", "una tecla desconocida no mueve nada");
    }

    [Fact]
    public async Task Elegir_del_desplegable_cambia_el_documento_del_lateral()
    {
        var cut = Renderizar(TresQueSeResuelven());

        await SeleccionarEnDesplegableAsync(cut, "Tercera");

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Tercera");
        Selector(cut).Should().Be("3 de 3");
        cut.FindAll(".revision-ia-pop").Should().BeEmpty("elegir cierra el desplegable");
    }

    [Fact]
    public void Consulta_ve_la_revision_pero_sin_ninguna_accion_de_escritura()
    {
        Registrar(new MediadorFalso { Revisiones = [Revision("Primera", 97, Vence)] });
        this.ConRolDeEscritura(Roles.Consulta);

        var cut = Render<RevisionIaTab>();

        cut.Find(".revision-ia-selector-textos").TextContent.Should().Contain("Primera");
        var textos = cut.FindAll("button").Select(b => b.TextContent.Trim()).ToList();
        textos.Should().NotContain("Aceptar detección").And.NotContain("Corregir a mano…").And.NotContain("Descartar la lectura");
        textos.Should().NotContain(t => t.StartsWith("Confirmar todos"));
    }

    [Fact]
    public async Task Al_resolver_la_ultima_se_muestra_que_no_quedan_documentos_por_revisar()
    {
        var mediador = new MediadorFalso { Revisiones = [Revision("Única", 80, Vence)] };
        mediador.AlAplicar = c =>
        {
            mediador.Revisiones = [];
            return Result.Exito();
        };
        var cut = Renderizar(mediador);

        await Boton(cut, "Aceptar detección").ClickAsync(new MouseEventArgs());

        cut.Markup.Should().Contain("No quedan documentos por revisar");
        cut.FindAll("a").Should().ContainSingle(a => a.TextContent.Trim() == "Volver a Documentos" && a.GetAttribute("href") == "/documentos" && !a.ClassList.Contains("revision-ia-volver"));
    }

    [Fact]
    public void Sin_pendientes_desde_el_principio_se_dice_sin_revisiones_pendientes()
    {
        var cut = Renderizar(new MediadorFalso());

        cut.Markup.Should().Contain("Sin revisiones pendientes").And.NotContain("No quedan documentos por revisar");
        cut.FindAll(".revision-ia-lateral").Should().BeEmpty();
    }

    [Fact]
    public async Task Confirmar_todos_cuenta_solo_las_confirmables_de_la_lista_visible()
    {
        var alta = Revision("Alta", 97, Vence);
        var mediador = new MediadorFalso { Revisiones = [Revision("Media", 70, Vence), alta] };
        var cut = Renderizar(mediador);

        await Boton(cut, "Confirmar todos los ≥95 % (1)").ClickAsync(new MouseEventArgs());
        await ConfirmarBloqueAsync(cut);

        mediador.Enviadas.OfType<CaeManager.Application.Documentos.Commands.AceptarDeteccionesIaEnBloque.AceptarDeteccionesIaEnBloqueCommand>()
            .Should().ContainSingle().Which.RevisionIds.Should().Equal(alta.Id);
    }
}

using Bunit;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (lote 1): el panel de Subcontrata pinta un solo aviso de cambios sin guardar para la
/// edición en línea (Información y credenciales) y el drawer de verificación externa. La X
/// del drawer pregunta solo por el drawer: la edición en línea no se pierde al cerrarlo.
/// </summary>
public partial class Subcontrata360Gen2Tests
{
    private NavigationManager Navegacion => Services.GetRequiredService<NavigationManager>();

    private static bool PreguntaDescartar(IRenderedComponent<CaeManager.Web.Features.Subcontratas.Components.SubcontrataWorkspacePanel> cut) =>
        cut.FindAll("button").Any(b => b.TextContent.Trim() == "Descartar cambios");

    [Fact]
    public async Task Aviso_la_identidad_editada_pregunta_al_salir()
    {
        var cut = await AbrirEdicionAsync(PrepararEdicion(new MediatorFalso()));
        await Control(cut, "Razón social").InputAsync(new ChangeEventArgs { Value = "Pinturas Lauburu Norte S.A." });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_la_credencial_editada_pregunta_al_salir()
    {
        var cut = await AbrirEdicionAsync(PrepararEdicion(new MediatorFalso()));
        await Control(cut, "Usuario").InputAsync(new ChangeEventArgs { Value = "lauburu.admin" });

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_abrir_la_edicion_sin_tocar_nada_no_pregunta()
    {
        var cut = await AbrirEdicionAsync(PrepararEdicion(new MediatorFalso()));

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "la identidad y la credencial precargadas no son un cambio de quien edita");
    }

    private async Task<IRenderedComponent<CaeManager.Web.Features.Subcontratas.Components.SubcontrataWorkspacePanel>> AbrirVerificacionAsync()
    {
        var escena = PrepararRegistro(new MediatorFalso());
        var cut = Renderizar(escena.Id, "supervision");
        await Boton(cut, "+ Registrar verificación").ClickAsync(new MouseEventArgs());
        return cut;
    }

    [Fact]
    public async Task Aviso_la_verificacion_a_medias_pregunta_al_salir_y_al_cerrar_el_drawer()
    {
        var cut = await AbrirVerificacionAsync();
        await Control(cut, "Observaciones").InputAsync(new ChangeEventArgs { Value = "Verificado por teléfono." });

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());
        PreguntaDescartar(cut).Should().BeTrue("cerrar el drawer con cambios pregunta «¿Descartar cambios?»");
        await cut.FindAll("button").First(b => b.TextContent.Trim() == "Seguir editando").ClickAsync(new MouseEventArgs());

        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }

    [Fact]
    public async Task Aviso_la_verificacion_sin_tocar_se_cierra_y_se_sale_sin_preguntar()
    {
        var cut = await AbrirVerificacionAsync();

        await cut.SalirYComprobarQueNoPreguntaAsync(Navegacion, "el centro y la fecha preseleccionados no son un cambio de quien edita");
    }

    /// <summary>D-05: «Cancelar» del drawer de verificación cierra como la X.</summary>
    [Fact]
    public async Task Cancelar_la_verificacion_a_medias_pregunta_y_sin_tocar_cierra()
    {
        var cut = await AbrirVerificacionAsync();
        await cut.ComprobarQueCancelarSinCambiosCierraAsync(".drawer-pie", ".drawer-panel");

        await Boton(cut, "+ Registrar verificación").ClickAsync(new MouseEventArgs());
        await Control(cut, "Observaciones").InputAsync(new ChangeEventArgs { Value = "Verificado por teléfono." });
        await cut.PulsarCancelarDelPieAsync(".drawer-pie");

        await cut.ComprobarQuePreguntaYDescartarAsync(".drawer-panel");
    }

    [Fact]
    public async Task Aviso_cerrar_el_drawer_no_pregunta_por_la_edicion_en_linea()
    {
        var escena = PrepararRegistro(new MediatorFalso());
        var cut = Renderizar(escena.Id, "informacion", _ => { });
        await Boton(cut, "Editar identidad").ClickAsync(new MouseEventArgs());
        await Control(cut, "Razón social").InputAsync(new ChangeEventArgs { Value = "Pinturas Lauburu Norte S.A." });
        cut.Render(p => p.Add(x => x.PestanaActiva, "supervision"));
        await Boton(cut, "+ Registrar verificación").ClickAsync(new MouseEventArgs());

        await cut.Find(".drawer-cerrar").ClickAsync(new MouseEventArgs());

        PreguntaDescartar(cut).Should().BeFalse("el drawer no tiene cambios; la edición en línea sigue en el panel");
        await cut.SalirYComprobarQuePreguntaAsync(Navegacion);
    }
}

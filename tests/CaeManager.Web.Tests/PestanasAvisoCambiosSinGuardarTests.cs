using Bunit;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>
/// P1-E2b (a): cambiar de pestaña desmonta su contenido antes de que cambie la URL, así que
/// el NavigationLock de un formulario de dentro no llega a tiempo. <see cref="Pestanas"/>
/// ofrece un <see cref="AmbitoCambiosSinGuardar"/> a su contenido y, con cambios sin
/// guardar dentro, pregunta con el mismo aviso antes de avisar del cambio de pestaña. Cubre
/// así el Context Workspace (ContextWorkspaceService.CambiarPestanaAsync) y /documentos
/// (Documentos.CambiarPestana), que solo cambian cuando Pestanas se lo pide.
/// </summary>
public class PestanasAvisoCambiosSinGuardarTests : BunitContext
{
    private static readonly IReadOnlyList<PestanaDefinicion> Definiciones =
    [
        new("informacion", "Información"),
        new("firma", "Firma"),
    ];

    private readonly List<string> _cambios = [];
    private bool _hayCambios;
    private int _descartes;

    public PestanasAvisoCambiosSinGuardarTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private RenderFragment FormularioConAviso => b =>
    {
        b.OpenComponent<AvisoCambiosSinGuardar>(0);
        b.AddAttribute(1, nameof(AvisoCambiosSinGuardar.HayCambios), (Func<bool>)(() => _hayCambios));
        b.AddAttribute(2, nameof(AvisoCambiosSinGuardar.AlDescartar),
            EventCallback.Factory.Create(this, () => _descartes++));
        b.CloseComponent();
    };

    private IRenderedComponent<Pestanas> Renderizar(RenderFragment contenido) =>
        Render<Pestanas>(p => p
            .Add(x => x.Definiciones, Definiciones)
            .Add(x => x.PestanaActiva, "informacion")
            .Add(x => x.PestanaActivaChanged, id => _cambios.Add(id))
            .Add(x => x.ChildContent, contenido));

    private static Task PulsarPestanaAsync<T>(IRenderedComponent<T> cut, string etiqueta) where T : IComponent =>
        cut.FindAll(".pestanas-boton").First(b => b.TextContent.Trim() == etiqueta).ClickAsync(new MouseEventArgs());

    private static Task PulsarEnElAvisoAsync<T>(IRenderedComponent<T> cut, string texto) where T : IComponent =>
        cut.FindAll(".modal-pie button").Single(b => b.TextContent.Trim() == texto).ClickAsync(new MouseEventArgs());

    [Fact]
    public async Task Sin_cambios_cambia_de_pestana_sin_preguntar()
    {
        var cut = Renderizar(FormularioConAviso);

        await PulsarPestanaAsync(cut, "Firma");

        _cambios.Should().Equal("firma");
        cut.FindAll(".modal-contenido").Should().BeEmpty();
    }

    [Fact]
    public async Task Con_cambios_pregunta_y_seguir_editando_no_cambia_de_pestana()
    {
        _hayCambios = true;
        var cut = Renderizar(FormularioConAviso);

        // El clic queda esperando la respuesta: no se espera aquí.
        var clic = PulsarPestanaAsync(cut, "Firma");

        _cambios.Should().BeEmpty("con cambios sin guardar se pregunta antes de desmontar la pestaña");
        cut.FindAll(".modal-contenido").Should().ContainSingle();
        await PulsarEnElAvisoAsync(cut, "Seguir editando");
        await clic;

        _cambios.Should().BeEmpty();
        _descartes.Should().Be(0);
    }

    [Fact]
    public async Task Con_cambios_salir_y_descartar_cambia_de_pestana()
    {
        _hayCambios = true;
        var cut = Renderizar(FormularioConAviso);

        var clic = PulsarPestanaAsync(cut, "Firma");
        await PulsarEnElAvisoAsync(cut, "Salir y descartar");
        await clic;

        _cambios.Should().Equal("firma");
        _descartes.Should().Be(1, "el formulario limpia lo que dejaría a medias antes de desmontarse");
    }

    /// <summary>
    /// Pestañas dentro de pestañas (las subpestañas de Plantillas dentro de /documentos):
    /// cambiar la pestaña de fuera también desmonta el formulario de dentro, así que pregunta.
    /// </summary>
    [Fact]
    public async Task Un_formulario_en_pestanas_anidadas_tambien_frena_la_pestana_de_fuera()
    {
        _hayCambios = true;
        RenderFragment interiores = b =>
        {
            b.OpenComponent<Pestanas>(0);
            b.AddAttribute(1, nameof(Pestanas.Definiciones), (IReadOnlyList<PestanaDefinicion>)[new("catalogo", "Catálogo"), new("generados", "Generados")]);
            b.AddAttribute(2, nameof(Pestanas.PestanaActiva), "catalogo");
            b.AddAttribute(3, nameof(Pestanas.ChildContent), FormularioConAviso);
            b.CloseComponent();
        };
        var cut = Renderizar(interiores);

        var clic = PulsarPestanaAsync(cut, "Firma");

        _cambios.Should().BeEmpty();
        await PulsarEnElAvisoAsync(cut, "Seguir editando");
        await clic;
        _cambios.Should().BeEmpty();
    }

    /// <summary>El aviso fuera del contenido de las pestañas (un formulario que sobrevive al cambio) no frena el cambio.</summary>
    [Fact]
    public async Task Un_aviso_fuera_de_las_pestanas_no_frena_el_cambio()
    {
        _hayCambios = true;
        var cut = Render(b =>
        {
            b.AddContent(0, FormularioConAviso);
            b.OpenComponent<Pestanas>(1);
            b.AddAttribute(2, nameof(Pestanas.Definiciones), Definiciones);
            b.AddAttribute(3, nameof(Pestanas.PestanaActiva), "informacion");
            b.AddAttribute(4, nameof(Pestanas.PestanaActivaChanged),
                EventCallback.Factory.Create<string>(this, id => _cambios.Add(id)));
            b.CloseComponent();
        });

        await PulsarPestanaAsync(cut, "Firma");

        _cambios.Should().Equal("firma");
    }
}

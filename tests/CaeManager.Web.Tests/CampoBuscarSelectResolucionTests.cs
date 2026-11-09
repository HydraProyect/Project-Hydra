using Bunit;
using CaeManager.Application.Trabajadores.Queries.ObtenerTrabajadoresParaSelector;
using CaeManager.Web.Components.DesignSystem;
using FluentAssertions;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using static CaeManager.Web.Tests.AyudasCampoBuscarSelect;

namespace CaeManager.Web.Tests;

/// <summary>
/// <see cref="CampoBuscarSelect"/> es el combobox del kit con buscador: escribir filtra, pulsar una opción elige su
/// Id. Lo que fijaba el datalist sigue en pie, ahora por construcción: nunca se asigna en silencio a un homónimo
/// (cada uno es una opción distinta), un texto sin coincidencia no elige nada, y un Id recibido antes que sus
/// opciones se pinta cuando llegan.
/// </summary>
public class CampoBuscarSelectResolucionTests : BunitContext
{
    private readonly List<string> _valores = [];

    public CampoBuscarSelectResolucionTests()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
    }

    private IRenderedComponent<CampoBuscarSelect> Renderizar(params OpcionBuscable[] opciones) =>
        Render<CampoBuscarSelect>(p => p
            .Add(c => c.Etiqueta, "Trabajador")
            .Add(c => c.Marcador, "Escribe un nombre o alias para buscar…")
            .Add(c => c.Opciones, opciones)
            .Add(c => c.ValorChanged, EventCallback.Factory.Create<string>(this, v => _valores.Add(v))));

    [Fact]
    public void Es_un_solo_combobox_con_la_etiqueta_visible_y_sin_datalist()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"));

        var disparador = Disparador(cut);
        disparador.GetAttribute("aria-haspopup").Should().Be("listbox");
        cut.Find($"label[for='{disparador.Id}']").TextContent.Trim().Should().Be("Trabajador");
        TextoVisible(cut).Should().Be("Escribe un nombre o alias para buscar…");
        cut.FindAll("datalist").Should().BeEmpty();
    }

    [Fact]
    public async Task Escribir_filtra_sin_mayusculas_ni_tildes_y_no_elige_por_si_solo()
    {
        var cut = Renderizar(new OpcionBuscable("a", "José Ruiz"), new OpcionBuscable("b", "Luis Gil"));

        await BuscarAsync(cut, "jose");

        Opciones(cut).Select(o => o.TextContent.Trim()).Should().Equal("José Ruiz");
        _valores.Should().BeEmpty("el texto del buscador es un filtro, no una elección");
    }

    [Fact]
    public async Task Pulsar_una_opcion_elige_su_Id()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Luis Gil"));

        await ElegirAsync(cut, "Luis Gil");

        _valores.Should().Equal("b");
    }

    [Fact]
    public async Task Dos_opciones_con_el_mismo_texto_son_dos_elecciones_distintas()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Ana Ruiz"));

        await BuscarAsync(cut, "Ana Ruiz");
        Opciones(cut).Should().HaveCount(2, "ninguna tapa a la otra");

        await ElegirAsync(cut, "Ana Ruiz", posicion: 1);
        _valores.Should().Equal("b");
    }

    /// <summary>
    /// Caso adversario de la revisión Codex (ronda 1): con las etiquetas de
    /// <see cref="EtiquetasSelectorTrabajador"/>, cada una elige a su Trabajador, aunque un nombre literal imite la
    /// etiqueta con Id de otro.
    /// </summary>
    [Fact]
    public async Task Cada_etiqueta_de_Trabajador_elige_su_Id_aunque_un_nombre_imite_la_de_otro()
    {
        var idA = Guid.Parse("00000000-0000-0000-0000-00000000000a");
        var trabajadores = new[]
        {
            new TrabajadorSelectorDto(idA, "Ana Ruiz", null, null),
            new TrabajadorSelectorDto(Guid.Parse("00000000-0000-0000-0000-00000000000b"), "Ana Ruiz", null, null),
            new TrabajadorSelectorDto(Guid.Parse("00000000-0000-0000-0000-00000000000c"), "Ana Ruiz [1]", null, null),
            new TrabajadorSelectorDto(Guid.Parse("00000000-0000-0000-0000-00000000000d"), $"Ana Ruiz [1] [{idA:N}]", null, null),
        };
        var etiquetas = EtiquetasSelectorTrabajador.Construir(trabajadores);
        var cut = Renderizar(etiquetas.Select(e => new OpcionBuscable(e.Id.ToString(), e.Texto)).ToArray());

        foreach (var etiqueta in etiquetas)
        {
            _valores.Clear();
            await ElegirAsync(cut, etiqueta.Texto);
            _valores.Should().Equal(etiqueta.Id.ToString());
        }
    }

    [Fact]
    public async Task Un_texto_sin_coincidencia_no_deja_nada_que_elegir()
    {
        var cut = Renderizar(new OpcionBuscable("a", "Ana Ruiz"));

        await BuscarAsync(cut, "Pedro");

        Opciones(cut).Should().BeEmpty();
        cut.FindAll("[role=status]").Should().NotBeEmpty("se anuncia que no hay resultados");
        _valores.Should().BeEmpty();
    }

    /// <summary>
    /// Defecto L1 del piloto Outbound (2026-10-08): quien preselecciona una entidad antes de haber
    /// cargado la lista (el lote de reclamación abierto desde «Pedir») dejaba el campo vacío para
    /// siempre. El combobox pinta el texto de la opción del Valor en cada render, así que aparece al llegar.
    /// </summary>
    [Fact]
    public void Un_Id_recibido_antes_que_sus_opciones_se_pinta_cuando_llegan()
    {
        var cut = Render<CampoBuscarSelect>(p => p
            .Add(c => c.Etiqueta, "Trabajador")
            .Add(c => c.Marcador, "Busca…")
            .Add(c => c.Valor, "b")
            .Add(c => c.Opciones, []));
        TextoVisible(cut).Should().Be("Busca…", "control: sin opciones todavía no hay texto que pintar");

        cut.Render(p => p.Add(c => c.Opciones, [new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Luis Gil")]));

        TextoVisible(cut).Should().Be("Luis Gil");
    }

    /// <summary>
    /// La otra cara: si las opciones llegan mientras la persona busca, no se le borra la búsqueda ni se elige nada
    /// en su nombre.
    /// </summary>
    [Fact]
    public async Task Si_las_opciones_llegan_mientras_se_busca_no_se_pisa_la_busqueda()
    {
        var cut = Render<CampoBuscarSelect>(p => p
            .Add(c => c.Etiqueta, "Trabajador")
            .Add(c => c.Valor, "b")
            .Add(c => c.Opciones, [])
            .Add(c => c.ValorChanged, EventCallback.Factory.Create<string>(this, v => _valores.Add(v))));

        await BuscarAsync(cut, "Ana");
        cut.Render(p => p.Add(c => c.Opciones, [new OpcionBuscable("a", "Ana Ruiz"), new OpcionBuscable("b", "Luis Gil")]));

        Opciones(cut).Select(o => o.TextContent.Trim()).Should().Equal("Ana Ruiz");
        _valores.Should().BeEmpty();
    }
}

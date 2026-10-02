using Bunit;
using CaeManager.Application.TiposDocumento.Queries.ObtenerTiposDocumento;
using CaeManager.Domain.Documentos;
using CaeManager.Web.Components.DesignSystem;
using CaeManager.Web.Features.Documentos.Components;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace CaeManager.Web.Tests;

/// <summary>D-31 del recorrido en vivo del 2026-10-01: selector de tipo con búsqueda y agrupado; pestañas que envuelven; input de archivo con nombre.</summary>
public class SelectorTipoDocumentoTests : BunitContext
{
    public SelectorTipoDocumentoTests() => Services.AddLocalization();

    private static TipoDocumentoListaDto Tipo(string nombre, RequisitoDocumental requisito, int orden, params string[] aliases) =>
        new(Guid.NewGuid(), nombre, null, false, orden, AmbitoAplicacion.Trabajador, requisito, NaturalezaJuridica.ObligacionLegal,
            null, null, null, null, false, false, false, default, aliases);

    private static IReadOnlyList<TipoDocumentoListaDto> Catalogo()
    {
        var tipos = new List<TipoDocumentoListaDto>
        {
            Tipo("Reconocimiento médico", RequisitoDocumental.Si, 1, "Aptitud médica"),
            Tipo("Formación PRL", RequisitoDocumental.Si, 2),
            Tipo("REA", RequisitoDocumental.Condicional, 3),
            Tipo("Certificado A1", RequisitoDocumental.Condicional, 4),
            Tipo("Otro documento", RequisitoDocumental.No, 5),
        };
        tipos.AddRange(Enumerable.Range(1, 5).Select(n => Tipo($"Extra {n}", RequisitoDocumental.No, 10 + n)));
        return tipos;
    }

    private IRenderedComponent<SelectorTipoDocumento> Renderizar(string valor = "") =>
        Render<SelectorTipoDocumento>(p => p.Add(c => c.Tipos, Catalogo()).Add(c => c.Valor, valor));

    [Fact]
    public void Agrupa_por_peso_del_requisito_y_ordena_dentro_de_cada_grupo()
    {
        var cut = Renderizar();

        cut.FindAll("optgroup").Select(g => g.GetAttribute("label")).Should()
            .Equal("Se piden siempre", "Según actividad o situación", "Otros");
        cut.FindAll("optgroup")[0].QuerySelectorAll("option").Select(o => o.TextContent).Should()
            .Equal("Reconocimiento médico", "Formación PRL");
    }

    [Fact]
    public void El_buscador_filtra_por_nombre_y_por_alias_y_avisa_si_no_hay_coincidencias()
    {
        var cut = Renderizar();

        cut.Find("input[type=text]").Input("aptitud");
        cut.WaitForAssertion(() => cut.FindAll("option").Select(o => o.TextContent).Should()
            .Equal("Selecciona un tipo de documento…", "Reconocimiento médico"));

        cut.Find("input[type=text]").Input("zzz-no-existe");
        cut.WaitForAssertion(() => cut.Find("[role=status]").TextContent.Should().Contain("Ningún tipo coincide"));
    }

    [Fact]
    public void El_tipo_elegido_sobrevive_al_filtro_y_los_demas_no()
    {
        var catalogo = Catalogo();
        var elegido = catalogo.Single(t => t.Nombre == "Formación PRL");
        var cut = Render<SelectorTipoDocumento>(p => p.Add(c => c.Tipos, catalogo).Add(c => c.Valor, elegido.Id.ToString()));

        cut.Find("input[type=text]").Input("médico");

        cut.WaitForAssertion(() => cut.FindAll("option").Select(o => o.TextContent).Should()
            .Equal("Selecciona un tipo de documento…", "Reconocimiento médico", "Formación PRL"));
        cut.Find("select").GetAttribute("value").Should().Be(elegido.Id.ToString());
    }

    [Fact]
    public void Con_pocos_tipos_no_hay_buscador()
    {
        var cut = Render<SelectorTipoDocumento>(p => p.Add(c => c.Tipos, Catalogo().Take(3).ToList()));

        cut.FindAll("input[type=text]").Should().BeEmpty();
        cut.FindAll("select").Should().ContainSingle();
    }

    [Fact]
    public void El_input_de_archivo_de_la_zona_de_soltar_tiene_nombre_accesible()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;

        var cut = Render<ZonaSoltarArchivo>();

        cut.Find("input[type=file]").GetAttribute("aria-label").Should().Be("Suelta tu archivo aquí");
    }

    [Fact]
    public void Las_pestanas_envuelven_solo_si_se_pide()
    {
        IReadOnlyList<PestanaDefinicion> definiciones = [new("a", "Uno"), new("b", "Dos")];

        Render<Pestanas>(p => p.Add(c => c.Definiciones, definiciones).Add(c => c.PestanaActiva, "a"))
            .FindAll(".pestanas-lista-envuelta").Should().BeEmpty();
        Render<Pestanas>(p => p.Add(c => c.Definiciones, definiciones).Add(c => c.PestanaActiva, "a").Add(c => c.Envolver, true))
            .FindAll(".pestanas-lista-envuelta").Should().ContainSingle();
    }
}

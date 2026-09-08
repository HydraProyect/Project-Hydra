using Bunit;
using CaeManager.Application.Dashboard.Queries;
using CaeManager.Web.Features.Dashboard;
using CaeManager.Web.Features.Dashboard.Components;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// Los logotipos de las plataformas CAE se mapean por el <c>Codigo</c> del
/// catálogo, no por el nombre. Uso nominativo aprobado por el propietario:
/// identifican a qué portal pertenece cada pendiente, no son reclamo comercial.
///
/// <para>
/// Los dos riesgos reales de este mapa son silenciosos, y por eso están los dos
/// primeros tests: un slug mal escrito no rompe nada —la fila se pinta sin
/// marca, como las 19 plataformas que no tienen— y un fichero que falte da un
/// 404 que solo se ve en la consola del navegador.
/// </para>
/// </summary>
public class LogoPlataformaTests : BunitContext
{
    /// <summary>
    /// Cada slug con logotipo tiene que existir en la semilla del catálogo. Sin
    /// esto, escribir "ecoordina" donde el catálogo dice "e-coordina" deja la
    /// marca sin pintar para siempre y nadie se entera.
    /// </summary>
    [Fact]
    public void Todo_codigo_con_logo_existe_en_la_semilla_del_catalogo()
    {
        var semilla = LeerSemillaDelCatalogo();

        semilla.Should().NotBeEmpty(
            "si el lector de la semilla no encuentra códigos, este test estaría verde por no mirar nada");

        LogoPlataforma.CodigosConLogo.Should().OnlyContain(c => semilla.Contains(c),
            "un slug que no está en el catálogo nunca casará: la marca no se pinta y no hay error");
    }

    /// <summary>El fichero tiene que estar donde el mapa dice, o el navegador se come un 404 en silencio.</summary>
    [Fact]
    public void Todo_logo_referenciado_existe_en_wwwroot()
    {
        var raiz = RaizDelRepositorio();

        foreach (var codigo in LogoPlataforma.CodigosConLogo)
        {
            var ruta = LogoPlataforma.Ruta(codigo);
            ruta.Should().NotBeNull();

            var fisica = Path.Combine(raiz, "src", "CaeManager.Web", "wwwroot",
                ruta!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

            File.Exists(fisica).Should().BeTrue($"«{codigo}» apunta a {ruta} y ahí no hay fichero");
            new FileInfo(fisica).Length.Should().BeGreaterThan(0, $"el logotipo de «{codigo}» está vacío");
        }
    }

    /// <summary>
    /// El caso normal: 23 proveedores en el catálogo y 4 marcas. Devolver null
    /// es una respuesta de primera clase, no un hueco.
    /// </summary>
    [Fact]
    public void Una_plataforma_sin_logo_se_pinta_igual_solo_con_su_nombre()
    {
        var cut = Render<FilaPlataforma>(p => p.Add(c => c.Plataforma,
            new PendientePorPlataformaDto(Guid.NewGuid(), "Metacontratas", 3, 0, "metacontratas")));

        cut.Markup.Should().Contain("Metacontratas");
        cut.FindAll("img").Should().BeEmpty("no hay marca para este proveedor, y eso es lo habitual");
    }

    [Fact]
    public void Una_plataforma_con_logo_lo_pinta_sin_repetir_el_nombre_al_lector_de_pantalla()
    {
        var cut = Render<FilaPlataforma>(p => p.Add(c => c.Plataforma,
            new PendientePorPlataformaDto(Guid.NewGuid(), "Dokify", 2, 1, "dokify")));

        var img = cut.Find("img.fila-plataforma-logo");
        img.GetAttribute("src").Should().Be("/img/plataformas/dokify.jpg");
        img.GetAttribute("alt").Should().BeEmpty("el nombre va al lado en texto: anunciarlo dos veces solo estorba");
        img.GetAttribute("aria-hidden").Should().Be("true");

        cut.Markup.Should().Contain("Dokify");
    }

    /// <summary>
    /// <c>Grupo</c> agrupa «Twind (CTAIMA Group)» con Twind, CTAIMACAE legacy y
    /// e-coordina, pero el dominio dice que el grupo es <b>solo para
    /// analítica, nunca lógica operativa</b>. Twind no hereda la marca de CTAIMA.
    /// </summary>
    [Fact]
    public void Compartir_grupo_comercial_no_hereda_el_logotipo()
    {
        LogoPlataforma.Ruta("twind").Should().BeNull(
            "son proveedores separados aunque compartan marca: el grupo no es lógica operativa");
        LogoPlataforma.Ruta("ctaimacae-legacy").Should().NotBeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("proveedor-que-no-existe")]
    public void Un_codigo_vacio_o_desconocido_no_devuelve_ruta(string? codigo) =>
        LogoPlataforma.Ruta(codigo).Should().BeNull();

    private static HashSet<string> LeerSemillaDelCatalogo()
    {
        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure",
            "Persistence", "Seed", "ProveedorPlataformaCaeSeedData.cs");

        if (!File.Exists(ruta)) return [];

        return System.Text.RegularExpressions.Regex
            .Matches(File.ReadAllText(ruta), "\"(?<codigo>[a-z0-9][a-z0-9-]*)\"\\s*,\\s*\"")
            .Select(m => m.Groups["codigo"].Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string RaizDelRepositorio()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "CaeManager.slnx")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? AppContext.BaseDirectory;
    }
}

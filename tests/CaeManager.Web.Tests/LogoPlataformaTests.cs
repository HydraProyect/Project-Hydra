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
/// marca, como las pocas plataformas que no tienen— y un fichero que falte da un
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
    /// Quedan proveedores sin marca oficial localizable (ver
    /// <see cref="SinLogoDocumentado"/>). Devolver null es una respuesta de
    /// primera clase, no un hueco.
    /// </summary>
    [Fact]
    public void Una_plataforma_sin_logo_se_pinta_igual_solo_con_su_nombre()
    {
        var cut = Render<FilaPlataforma>(p => p.Add(c => c.Plataforma,
            new PendientePorPlataformaDto(Guid.NewGuid(), "Arch", 3, 0, "arch")));

        cut.Markup.Should().Contain("Arch");
        cut.FindAll("img").Should().BeEmpty("no hay marca para este proveedor, y eso es lo habitual");
    }

    /// <summary>
    /// La fila era el único bloque de Inicio sin salida: el propietario la pulsó
    /// en staging y no pasaba nada. Debe ser un enlace real (navegable con
    /// teclado y con clic central) a la pestaña que ya existe.
    /// </summary>
    [Fact]
    public void La_fila_de_plataforma_es_un_enlace_a_la_pestana_plataformas_cae_de_documentos()
    {
        var cut = Render<FilaPlataforma>(p => p.Add(c => c.Plataforma,
            new PendientePorPlataformaDto(Guid.NewGuid(), "Nalanda", 27, 0, "nalanda")));

        var fila = cut.Find(".fila-plataforma");
        fila.TagName.Should().Be("A");
        fila.GetAttribute("href").Should().Be("/documentos?pestana=plataforma");
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
        LogoPlataforma.Ruta("twind").Should().NotBe(LogoPlataforma.Ruta("ctaimacae-legacy"),
            "son proveedores separados aunque compartan marca: cada slug lleva el suyo y el grupo no es lógica operativa");
        LogoPlataforma.Ruta("twind").Should().NotBeNull();
        LogoPlataforma.Ruta("ctaimacae-legacy").Should().NotBeNull();
    }

    /// <summary>
    /// Proveedores del catálogo sin marca oficial localizable el 2026-10-02: el
    /// dominio de Arch está en venta, el de Opground no resuelve y Norprevención
    /// solo tiene el logotipo del grupo matriz. (Valora ya no figura aquí: pasó a
    /// ser Avanta Prevención y tiene su marca oficial, ver el test siguiente.)
    /// Con este test, un proveedor nuevo del catálogo obliga a decidir: añadir su
    /// marca o anotarlo aquí.
    /// </summary>
    private static readonly string[] SinLogoDocumentado = ["arch", "opground", "norprevencion"];

    [Fact]
    public void Todo_proveedor_del_catalogo_tiene_logo_o_esta_documentado_sin_el()
    {
        var semilla = LeerSemillaDelCatalogo();
        semilla.Should().NotBeEmpty("sin códigos leídos este test no mira nada");

        var sinDecidir = semilla
            .Where(c => LogoPlataforma.Ruta(c) is null && !SinLogoDocumentado.Contains(c, StringComparer.OrdinalIgnoreCase))
            .ToList();

        sinDecidir.Should().BeEmpty("cada proveedor del catálogo o tiene marca o está documentado como sin ella");
        SinLogoDocumentado.Should().OnlyContain(c => semilla.Contains(c) && LogoPlataforma.Ruta(c) == null,
            "la lista de excepciones no puede quedarse con slugs que ya no existen o que ya tienen marca");
    }

    /// <summary>
    /// Valora Prevención pasó a ser Avanta Prevención. El <c>Codigo</c> «valora» es el identificador
    /// estable y no cambia: la marca se engancha a ese slug, no al nombre visible.
    /// </summary>
    [Fact]
    public void El_slug_valora_pinta_el_logotipo_de_Avanta_Prevencion()
    {
        LogoPlataforma.Ruta("valora").Should().Be("/img/plataformas/avanta.png");

        var cut = Render<FilaPlataforma>(p => p.Add(c => c.Plataforma,
            new PendientePorPlataformaDto(Guid.NewGuid(), "Avanta Prevención", 3, 0, "valora")));

        cut.Find("img.fila-plataforma-logo").GetAttribute("src").Should().Be("/img/plataformas/avanta.png");
        cut.Markup.Should().Contain("Avanta Prevención");
    }

    /// <summary>
    /// El nombre del catálogo es el visible: ninguna fila de la semilla puede seguir llamándose «Valora»,
    /// y el slug sigue siendo «valora». Lee la semilla como texto (como los demás tests de este fichero).
    /// </summary>
    [Fact]
    public void La_semilla_llama_Avanta_Prevencion_al_proveedor_de_slug_valora()
    {
        var ruta = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Infrastructure",
            "Persistence", "Seed", "ProveedorPlataformaCaeSeedData.cs");
        File.Exists(ruta).Should().BeTrue();

        var fila = File.ReadAllLines(ruta).Single(l => l.Contains("\"valora\"", StringComparison.Ordinal));
        fila.Should().Contain("\"Avanta Prevención\"").And.Contain("avantaprevencion.com");
        fila.Should().NotContain("\"Valora\"");
    }

    /// <summary>
    /// Regla de #1019 para las marcas nuevas: como mucho 288×112 px (cabecera IHDR del PNG, sin
    /// decodificarlo). Los ficheros anteriores a esa regla no se miden aquí.
    /// </summary>
    [Fact]
    public void El_logo_de_Avanta_respeta_el_maximo_de_288_por_112()
    {
        var fisica = Path.Combine(RaizDelRepositorio(), "src", "CaeManager.Web", "wwwroot", "img", "plataformas", "avanta.png");
        var cabecera = File.ReadAllBytes(fisica).Take(24).ToArray();

        cabecera.Take(8).Should().Equal(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);
        var ancho = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(cabecera.AsSpan(16, 4));
        var alto = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(cabecera.AsSpan(20, 4));

        ancho.Should().BeInRange(1, 288);
        alto.Should().BeInRange(1, 112);
    }

    /// <summary>La UI pinta como mucho 72×28 px: un fichero grande solo añade peso a la página de Inicio.</summary>
    [Fact]
    public void Los_logos_pesan_poco()
    {
        var raiz = RaizDelRepositorio();
        foreach (var codigo in LogoPlataforma.CodigosConLogo)
        {
            var fisica = Path.Combine(raiz, "src", "CaeManager.Web", "wwwroot",
                LogoPlataforma.Ruta(codigo)!.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));
            new FileInfo(fisica).Length.Should().BeLessThan(40_000, $"el logotipo de «{codigo}» es demasiado pesado para pintarse a 72×28");
        }
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

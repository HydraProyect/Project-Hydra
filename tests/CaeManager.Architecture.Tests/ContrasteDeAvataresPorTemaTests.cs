using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using static CaeManager.Architecture.Tests.ContrasteCss;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// Las iniciales de todo avatar de texto se leen —contraste WCAG 2.x &gt;= 4,5:1
/// entre el color de las letras y el del fondo— en los dos temas: claro y oscuro.
///
/// <para>
/// <b>Por qué hace falta.</b> Defecto de staging del 2026-10-02: en tema oscuro
/// el avatar de un Tenant beneficiario sin logo salía como un cuadrado liso sin
/// letras. <c>AvatarTenant</c> pintaba <c>--color-primary-100</c> de fondo y
/// <c>--color-primary-700</c> de letra; en oscuro <c>tokens.css</c> remapea el
/// 700 a <c>--color-primary-100</c> pero el 100 no se redefine, así que fondo y
/// letra resolvían a <c>#dcebff</c> (1,00:1). Los trinquetes de
/// <see cref="TokensDeFondoConVarianteEnAmbosTemasTests"/> y
/// <see cref="TokensDeTextoConVarianteEnAmbosTemasTests"/> miran que el token
/// cambie de tema, nunca el contraste del PAR: el 100 no es un escalón -50 ni
/// uno de los vigilados como texto, y por eso este defecto pasó entre los dos.
/// </para>
///
/// <para>
/// <b>Contrato efectivo.</b> Para cada regla de avatar de la lista, lee del
/// <c>.css</c> real el <c>background</c> y el <c>color</c> declarados
/// (fusionando la regla base con su variante, cuando la hay), resuelve cada
/// <c>var(--token)</c> contra <c>tokens.css</c> —el bloque del tema tiene
/// prioridad sobre <c>:root</c>, como en el navegador— y mide el contraste con
/// los valores resultantes. Es contraste de TEXTO sobre FONDO propio: no mira
/// el borde, ni el fondo que haya detrás del avatar, ni el logo cuando lo hay
/// (una imagen no tiene iniciales). «Sistema» no es un tercer caso: con
/// <c>prefers-color-scheme</c> desactivado en <c>tokens.css</c> resuelve a los
/// valores de <c>:root</c>, que son los de claro.
/// </para>
///
/// <para>
/// <b>Límite declarado.</b> Lee la primera regla cuyo selector es exactamente el de la
/// lista: un override por estado o contexto (<c>:hover</c>, <c>[aria-selected] .avatar-tenant</c>)
/// que cambie <c>color</c> o <c>background</c> no se mide (medido el 2026-10-02: no existe
/// ninguno). Solo entiende colores hexadecimales y
/// <c>var()</c>; un <c>color-mix()</c>, <c>oklch()</c> o literal <c>rgb()</c>
/// en un avatar hace fallar el test con un mensaje explícito en vez de pasarlo
/// en falso. Y la lista de avatares es explícita: un avatar nuevo cuya clase
/// contenga «avatar» y no esté en ella hace fallar
/// <see cref="Todo_selector_de_avatar_del_arbol_esta_cubierto_o_exento"/>.
/// </para>
/// </summary>
public class ContrasteDeAvataresPorTemaTests
{
    private const double UmbralAa = 4.5;

    private sealed record AvatarCubierto(string Nombre, string Fichero, string[] Selectores);

    /// <summary>
    /// Avatares de iniciales del árbol. Una variante (p. ej. «Todo») va como
    /// segundo selector: sus declaraciones sobrescriben a las de la base.
    /// </summary>
    private static readonly AvatarCubierto[] Avatares =
    [
        new("AvatarTenant (selector de empresa, cabecera, Configuración)",
            "Components/DesignSystem/AvatarTenant.razor.css", [".avatar-tenant"]),
        new("Fila de relación (trabajador de Empresa 360)",
            "Components/DesignSystem/FilaRelacion.razor.css", [".fila-relacion-avatar"]),
        new("Cartera de Mi trabajo",
            "Features/Bandeja/Pages/MiTrabajo.razor.css", [".mi-trabajo-cartera-avatar"]),
        new("Cartera de Mi trabajo, opción «Todo»",
            "Features/Bandeja/Pages/MiTrabajo.razor.css",
            [".mi-trabajo-cartera-avatar", ".mi-trabajo-cartera-avatar-todo"]),
        new("Iniciales de empresa en el alta de usuario",
            "Features/Usuarios/Pages/Usuarios.razor.css", [".avatar-iniciales-empresa"]),
        new("Avatar de la línea de tiempo de Comunicaciones",
            "Features/Comunicaciones/Components/UnifiedTimeline.razor.css", [".timeline-avatar"]),
        new("Avatar de la fila de la Bandeja",
            "Features/Comunicaciones/Pages/Bandeja.razor.css",
            [".bandeja-lista ::deep .bandeja-fila-avatar"]),
        new("Avatar de la columna de contexto de la Bandeja",
            "Features/Comunicaciones/Pages/Bandeja.razor.css", [".bandeja-cliente-avatar"]),
    ];

    /// <summary>
    /// Clases con «avatar» en el nombre que no llevan iniciales sobre fondo propio:
    /// variantes de tamaño o la imagen del logo, que no tiene letras.
    /// </summary>
    private static readonly HashSet<string> Exentas = new(StringComparer.Ordinal)
    {
        "avatar-tenant-pequeno",   // solo cambia tamaño, radio y fuente; hereda colores de .avatar-tenant
        "avatar-tenant-logo",      // imagen del logo: sin iniciales
        "mi-trabajo-cartera-avatar-todo", // variante cubierta arriba junto a su base
    };

    [Theory]
    [InlineData("oscuro")]
    [InlineData("claro")]
    public void Las_iniciales_de_cada_avatar_cumplen_AA_en_el_tema(string tema)
    {
        var tokens = Tokens.Desde(File.ReadAllText(RutaTokensCss()));
        tokens.Completo.Should().BeTrue("tokens.css debe declarar :root y los bloques de tema oscuro y claro");

        var fallos = new List<string>();
        foreach (var avatar in Avatares)
        {
            var ruta = Path.Combine(RaizWeb(), avatar.Fichero.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(ruta).Should().BeTrue($"{avatar.Fichero} debe existir (lista de avatares desfasada)");

            var (fondo, texto) = ColoresDeLaRegla(File.ReadAllText(ruta), avatar.Selectores);
            fondo.Should().NotBeNull($"{avatar.Nombre}: sin background propio no se puede medir el contraste");
            texto.Should().NotBeNull($"{avatar.Nombre}: sin color propio hereda un color que este test no ve");

            var ratio = Contraste(tokens.Resolver(fondo!, tema), tokens.Resolver(texto!, tema));
            if (ratio < UmbralAa)
                fallos.Add($"{avatar.Nombre} ({avatar.Fichero}): {ratio:0.00}:1 en {tema} " +
                           $"[fondo {fondo} / letra {texto}]");
        }

        string.Join("\n", fallos).Should().BeEmpty(
            "las iniciales de un avatar tienen que leerse (>= 4,5:1) en los dos temas; si fondo y letra " +
            "salen de tokens que no cambian de tema a la vez, usa un par con variante en tokens.css " +
            "(--color-avatar-fondo / --color-avatar-texto)");
    }

    [Fact]
    public void Todo_selector_de_avatar_del_arbol_esta_cubierto_o_exento()
    {
        var cubiertas = Avatares
            .SelectMany(a => a.Selectores)
            .SelectMany(s => Regex.Matches(s, @"\.([\w-]*avatar[\w-]*)").Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        var raiz = RaizWeb();
        var encontradas = Directory.EnumerateFiles(raiz, "*.css", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}lib{Path.DirectorySeparatorChar}"))
            .SelectMany(f => Regex.Matches(SinComentarios(File.ReadAllText(f)), @"\.([\w-]*avatar[\w-]*)")
                .Select(m => m.Groups[1].Value))
            .ToHashSet(StringComparer.Ordinal);

        encontradas.Should().NotBeEmpty("sin selectores localizados este test estaría en verde por no mirar nada");
        encontradas.Except(cubiertas).Except(Exentas).Should().BeEmpty(
            "un avatar nuevo hay que añadirlo a la lista de ContrasteDeAvataresPorTemaTests para que se mida " +
            "en los dos temas (o a Exentas, con el motivo, si no lleva iniciales sobre fondo propio)");
    }

    // ---- Sensibilidad: el instrumento tiene que ver el defecto de 2026-10-02 ----

    private const string TokensDelDefecto = """
        :root {
          --color-primary-100: #dcebff;
          --color-primary-700: #163a7d;
        }
        :root[data-theme='oscuro'] {
          --color-primary-700: var(--color-primary-100);
        }
        :root[data-theme='claro'] {
          --color-primary-700: #163a7d;
        }
        """;

    private const string CssConElPar = ".avatar { background: var(--color-primary-100); color: var(--color-primary-700); }";

    [Fact]
    public void El_instrumento_marca_el_par_primary_100_700_en_oscuro_pero_no_en_claro()
    {
        var tokens = Tokens.Desde(TokensDelDefecto);
        var (fondo, texto) = ColoresDeLaRegla(CssConElPar, [".avatar"]);

        Contraste(tokens.Resolver(fondo!, "oscuro"), tokens.Resolver(texto!, "oscuro"))
            .Should().BeLessThan(1.01, "es el defecto: letra y fondo son #dcebff");
        Contraste(tokens.Resolver(fondo!, "claro"), tokens.Resolver(texto!, "claro"))
            .Should().BeGreaterThan(UmbralAa, "control negativo: en claro el mismo par se lee (8,97:1)");
    }

    [Fact]
    public void El_instrumento_sigue_el_par_de_avatar_con_variante_por_tema()
    {
        const string corregido = """
            :root {
              --color-primary-50: #eef5ff;
              --color-primary-100: #dcebff;
              --color-primary-700: #163a7d;
              --color-avatar-fondo: var(--color-primary-100);
              --color-avatar-texto: var(--color-primary-700);
            }
            :root[data-theme='oscuro'] {
              --color-primary-50: #0e1d39;
              --color-primary-700: var(--color-primary-100);
              --color-avatar-fondo: var(--color-primary-50);
              --color-avatar-texto: var(--color-primary-700);
            }
            """;
        var tokens = Tokens.Desde(corregido);
        var (fondo, texto) = ColoresDeLaRegla(
            ".avatar { background: var(--color-avatar-fondo); color: var(--color-avatar-texto); }", [".avatar"]);

        Contraste(tokens.Resolver(fondo!, "oscuro"), tokens.Resolver(texto!, "oscuro")).Should().BeGreaterThan(13.0);
        Contraste(tokens.Resolver(fondo!, "claro"), tokens.Resolver(texto!, "claro")).Should().BeGreaterThan(8.9);
    }

    [Fact]
    public void Una_variante_sobrescribe_a_su_base()
    {
        const string css = """
            .base { background: var(--a); color: var(--b); }
            .base-variante { background: var(--c); }
            """;
        var (fondo, texto) = ColoresDeLaRegla(css, [".base", ".base-variante"]);

        fondo.Should().Be("var(--c)");
        texto.Should().Be("var(--b)", "la variante no declara color: se mantiene el de la base");
    }

    [Fact]
    public void Un_formato_de_color_no_soportado_hace_fallar_en_vez_de_pasar_en_falso()
    {
        var tokens = Tokens.Desde(":root { --x: color-mix(in srgb, red 10%, white); }");

        var act = () => tokens.Resolver("var(--x)", "claro");

        act.Should().Throw<Exception>().WithMessage("*no soportado*");
    }
}

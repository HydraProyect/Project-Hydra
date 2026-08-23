using System.Text.RegularExpressions;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <b>Las páginas de plataforma no filtran por rol de Identity.</b> Su autoridad
/// es la capacidad <c>AdminPlataforma</c> (A3), no el rol <c>Administrador</c>.
///
/// <para>
/// Mantener el atributo crearía un <b>AND</b> que no forma parte del contrato de
/// la capacidad: quien tuviera la concesión pero no el rol quedaría fuera. Y hay
/// un motivo operativo además del de principio — desde F2b-2 el claim de rol se
/// retira mientras hay sesión privilegiada, así que la UI dependería de una
/// autoridad que el plano 3 deliberadamente no conserva.
/// </para>
///
/// <para>
/// <b>Por qué esto y no un test de renderizado.</b> La propiedad "quien tiene
/// AdminPlataforma sin rol Administrador no queda bloqueado" ya no es de
/// comportamiento: es cierta porque el atributo <b>no existe</b>. Lo único que
/// hay que impedir es que alguien lo devuelva, y eso se congela por la forma.
/// Montar una suite de renderizado de páginas —hoy inexistente: <c>Web.Tests</c>
/// es de componentes y no usa <c>TestAuthorizationContext</c> en ningún sitio—
/// sería andamiaje desproporcionado para una rama puntual.
/// </para>
///
/// <para>
/// <b>Hueco declarado:</b> no hay prueba de renderizado de que la acción aparezca
/// con la capacidad y desaparezca sin ella. Esa visibilidad la sostiene
/// <c>EsAdministradorPlataformaQuery</c>, que comparte predicado exacto con el
/// comando que representa.
/// </para>
/// </summary>
public class PaginasDePlataformaSinGateDeRolTests
{
    /// <summary>
    /// Las páginas cuya autoridad es la capacidad, con su ruta. La lista sigue
    /// existiendo —una página de plataforma es una decisión, no un accidente—
    /// pero ya no es la única fuente: <see cref="Descubrir"/> la contrasta
    /// contra el árbol.
    /// </summary>
    private static readonly string[] PaginasDePlataforma =
    [
        "src/CaeManager.Web/Features/Comercial/Pages/EstadoComercial.razor",
        "src/CaeManager.Web/Features/Delegaciones/Pages/Delegaciones.razor",
        // A3-5: la puerta del acto fundacional. Su autoridad es la identidad
        // raíz designada por el despliegue, que no es el rol Administrador ni la
        // capacidad AdminPlataforma —esa es lo que se obtiene al cruzarla—.
        "src/CaeManager.Web/Features/Plataforma/Pages/Plataforma.razor",
    ];

    /// <summary>
    /// Un filtro por rol, <b>esté donde esté</b>.
    ///
    /// <para>
    /// La primera versión exigía el prefijo <c>@attribute</c>, así que solo veía
    /// el atributo escrito en el <c>.razor</c>. Demostrado por mutación: el mismo
    /// atributo en la clase parcial del code-behind
    /// </para>
    /// <code>
    /// [Microsoft.AspNetCore.Authorization.Authorize(Roles = "Administrador")]
    /// public partial class Delegaciones
    /// </code>
    /// <para>
    /// compila, Blazor lo respeta igual —la autorización se resuelve sobre el
    /// tipo del componente, no sobre el fichero donde se escribió— y el ratchet
    /// pasaba en verde. Ahora el patrón acepta el nombre cualificado o sin
    /// cualificar, con o sin <c>@attribute</c>, y con <c>Roles</c> en cualquier
    /// posición de la lista de argumentos.
    /// </para>
    /// </summary>
    private static readonly Regex GatePorRol = new(
        @"\[\s*(?:\w+\.)*Authorize\s*\(\s*[^)]*\bRoles\b", RegexOptions.Compiled);

    /// <summary>
    /// Los espacios de nombres cuya autoridad A3 movió a <c>AdminPlataforma</c>.
    /// Consumir uno de ellos desde una página es lo que la convierte en página de
    /// plataforma — no la carpeta donde vive ni su ruta, que no tienen prefijo
    /// común.
    /// </summary>
    private static readonly Regex AutoridadDePlataforma = new(
        @"CaeManager\.Application\.(?:Plataforma|Comercial)\b|EsAdministradorPlataforma", RegexOptions.Compiled);

    /// <summary>
    /// Toda página que consuma autoridad de plataforma tiene que estar en la
    /// lista.
    ///
    /// <para>
    /// Sin esto la lista era puramente manual: una <b>cuarta</b> página de
    /// plataforma nacía sin vigilancia, y el bloque de trabajo que queda —A4, B,
    /// C— va a crear varias. La regla de descubrimiento se contrastó contra el
    /// árbol: devuelve exactamente las tres declaradas, ni una más.
    /// </para>
    /// </summary>
    [Fact]
    public void La_lista_cubre_todas_las_paginas_que_consumen_autoridad_de_plataforma()
    {
        var descubiertas = Descubrir();

        descubiertas.Should().NotBeEmpty(
            "si el descubrimiento no encuentra ninguna página, ha dejado de observar el fenómeno y la " +
            "comparación de abajo pasaría en vacío");

        descubiertas.Should().BeEquivalentTo(PaginasDePlataforma,
            "una página que consume autoridad de plataforma se autoriza por capacidad; añadirla a la lista " +
            "en el mismo commit es la decisión que este ratchet obliga a tomar a la vista");
    }

    [Fact]
    public void Ninguna_pagina_de_plataforma_filtra_por_rol_de_Identity()
    {
        var raiz = RaizDelRepositorio();

        foreach (var pagina in PaginasDePlataforma)
        {
            var ruta = Path.Combine(raiz, pagina.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(ruta).Should().BeTrue($"{pagina} debe existir; si se movió, actualiza esta lista");

            // El .razor y su code-behind son el MISMO componente: el atributo
            // surte efecto en cualquiera de los dos.
            foreach (var fichero in FicherosDelComponente(ruta))
            {
                GatePorRol.IsMatch(File.ReadAllText(fichero)).Should().BeFalse(
                    $"{Path.GetFileName(fichero)} se autoriza por la capacidad AdminPlataforma, no por el rol " +
                    "Administrador: el claim de rol se retira bajo sesión privilegiada, así que volver a " +
                    "filtrar por él dejaría fuera exactamente a quien opera con privilegio de plataforma");
            }

            File.ReadAllText(ruta).Should().Contain("@attribute [Authorize]",
                $"{pagina} sigue exigiendo autenticación: quitar el rol no es abrir la página");
        }
    }

    /// <summary>El <c>.razor</c> y, si existe, su <c>.razor.cs</c>.</summary>
    private static IEnumerable<string> FicherosDelComponente(string rutaRazor)
    {
        yield return rutaRazor;

        var codeBehind = rutaRazor + ".cs";
        if (File.Exists(codeBehind)) yield return codeBehind;
    }

    private static List<string> Descubrir()
    {
        var raiz = RaizDelRepositorio();
        var web = Path.Combine(raiz, "src", "CaeManager.Web");

        return Directory
            .EnumerateFiles(web, "*.razor", SearchOption.AllDirectories)
            .Where(a => !a.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !a.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .Where(a => File.ReadAllText(a).Contains("@page", StringComparison.Ordinal))
            .Where(a => FicherosDelComponente(a).Any(f => AutoridadDePlataforma.IsMatch(File.ReadAllText(f))))
            .Select(a => Path.GetRelativePath(raiz, a).Replace(Path.DirectorySeparatorChar, '/'))
            .OrderBy(r => r)
            .ToList();
    }

    private static string RaizDelRepositorio()
    {
        var actual = new DirectoryInfo(AppContext.BaseDirectory);

        while (actual is not null && !File.Exists(Path.Combine(actual.FullName, "CaeManager.slnx")))
            actual = actual.Parent;

        if (actual is null)
            throw new InvalidOperationException(
                "No se encontró CaeManager.slnx subiendo desde " + AppContext.BaseDirectory);

        return actual.FullName;
    }
}

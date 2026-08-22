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
    private static readonly string[] PaginasDePlataforma =
    [
        "src/CaeManager.Web/Features/Comercial/Pages/EstadoComercial.razor",
        "src/CaeManager.Web/Features/Delegaciones/Pages/Delegaciones.razor",
    ];

    private static readonly Regex GatePorRol = new(
        @"@attribute\s*\[\s*Authorize\s*\(\s*Roles", RegexOptions.Compiled);

    [Fact]
    public void Ninguna_pagina_de_plataforma_filtra_por_rol_de_Identity()
    {
        var raiz = RaizDelRepositorio();

        foreach (var pagina in PaginasDePlataforma)
        {
            var ruta = Path.Combine(raiz, pagina.Replace('/', Path.DirectorySeparatorChar));
            File.Exists(ruta).Should().BeTrue($"{pagina} debe existir; si se movió, actualiza esta lista");

            var texto = File.ReadAllText(ruta);

            GatePorRol.IsMatch(texto).Should().BeFalse(
                $"{pagina} se autoriza por la capacidad AdminPlataforma, no por el rol Administrador: " +
                "el claim de rol se retira bajo sesión privilegiada, así que volver a filtrar por él " +
                "dejaría fuera exactamente a quien opera con privilegio de plataforma");

            texto.Should().Contain("@attribute [Authorize]",
                $"{pagina} sigue exigiendo autenticación: quitar el rol no es abrir la página");
        }
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

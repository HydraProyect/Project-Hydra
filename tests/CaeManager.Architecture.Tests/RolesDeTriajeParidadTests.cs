using System.Text.RegularExpressions;
using CaeManager.Application.Comunicaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// La cola de triaje de Comunicaciones la deciden cuatro listas de roles que no se alcanzan entre sí: quién entra en la
/// pantalla (<see cref="RolesComunicaciones.Gestion"/>, Web), quién ve el hilo sin ancla fuera del acceso total
/// (<see cref="AlcanceDatosService.RolesQueVenTriaje"/>, Infrastructure), quién se lleva sus adjuntos
/// (<see cref="AccesoATriage.Roles"/>, Application) y quién lee una alerta crítica (<c>AccesoAlertaCritica</c>, que reutiliza
/// la de Application). Ampliar una sin las otras dejaría un enlace que da una bandeja vacía, un adjunto que se descarga por
/// fuera de la pantalla o un hilo visible a un rol que la pantalla no deja entrar (D-6 b, R-3a).
/// </summary>
public class RolesDeTriajeParidadTests
{
    [Fact]
    public void Los_roles_que_ven_el_triaje_son_los_de_la_pantalla()
    {
        AlcanceDatosService.RolesQueVenTriaje
            .Should().BeEquivalentTo(RolesComunicaciones.Gestion.Split(','));
    }

    [Fact]
    public void Los_roles_de_triaje_de_Application_son_los_de_la_pantalla()
    {
        AccesoATriage.Roles
            .Should().BeEquivalentTo(RolesComunicaciones.Gestion.Split(','));
    }

    [Fact]
    public void La_lista_de_triaje_no_incluye_al_rol_Cliente_ni_a_un_rol_fuera_del_catalogo()
    {
        AlcanceDatosService.RolesQueVenTriaje.Should().NotContain(Roles.Cliente);
        AlcanceDatosService.RolesQueVenTriaje.Should().BeSubsetOf(Roles.Todos);
    }

    /// <summary>
    /// <c>AccesoAlertaCritica.PuedeLeerAsync</c> no tiene una lista propia: lee <see cref="AccesoATriage.Roles"/>. Si vuelve
    /// a escribir los roles a mano, este test lo detecta antes de que diverjan.
    /// </summary>
    [Fact]
    public void La_lectura_de_una_alerta_critica_usa_la_lista_de_triaje_y_no_una_propia()
    {
        var fuente = File.ReadAllText(Path.Combine(RaizDelRepositorio(),
            "src", "CaeManager.Application", "Comunicaciones", "AlertasCriticas", "AlertasCriticasRequests.cs"));
        var puedeLeer = Regex.Match(fuente, @"public async Task<bool> PuedeLeerAsync\(.*?\n    \}", RegexOptions.Singleline);

        puedeLeer.Success.Should().BeTrue("PuedeLeerAsync debe seguir existiendo en AccesoAlertaCritica");
        puedeLeer.Value.Should().Contain("AccesoATriage.Roles.Contains(");
        foreach (var rol in Roles.Todos)
            puedeLeer.Value.Should().NotContain($"\"{rol}\"", "la lista de roles vive en AccesoATriage.Roles");
    }

    private static string RaizDelRepositorio()
    {
        var directorio = new DirectoryInfo(AppContext.BaseDirectory);
        while (directorio is not null && directorio.GetFiles("*.slnx").Length == 0)
            directorio = directorio.Parent;
        return directorio?.FullName ?? throw new InvalidOperationException("No se encontró la raíz del repositorio (*.slnx).");
    }
}

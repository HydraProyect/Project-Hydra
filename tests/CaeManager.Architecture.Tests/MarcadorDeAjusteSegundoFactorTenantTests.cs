using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>IComandoDeAjusteSegundoFactorTenant</c> es la única puerta de escritura de una Sesión
/// Privilegiada con la capacidad <c>AjusteSegundoFactorTenant</c> (decisiones del 2026-11-08;
/// ADR-011 § 8.7, punto 3): <c>AutorizacionEscrituraBehavior</c> deja pasar a esa sesión cualquier
/// comando que la implemente. Un segundo comando marcado ampliaría en silencio lo que un Actor de
/// Plataforma TALVEG puede hacer en un Tenant, por ejemplo cambiar también el plazo de inactividad.
/// Mismo ratchet que <see cref="MarcadorDeConcesionAdministradorTests"/>.
/// </summary>
public class MarcadorDeAjusteSegundoFactorTenantTests
{
    [Fact]
    public void Solo_CambiarSegundoFactorDelTenantPorPlataformaCommand_implementa_el_marcador()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        var marcados = application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IComandoDeAjusteSegundoFactorTenant).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .OrderBy(x => x)
            .ToList();

        marcados.Should().Equal(
            ["CaeManager.Application.Plataforma.Commands.CambiarSegundoFactorDelTenantPorPlataforma.CambiarSegundoFactorDelTenantPorPlataformaCommand"],
            "cada comando marcado es algo que un Actor de Plataforma TALVEG puede escribir en un Tenant con esta capacidad; " +
            "añadir uno es ampliar la capacidad, y eso se decide y se documenta");
    }
}

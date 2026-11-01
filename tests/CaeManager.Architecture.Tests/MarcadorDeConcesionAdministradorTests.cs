using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>IComandoDeConcesionAdministrador</c> es la única puerta de escritura de una Sesión
/// Privilegiada con la capacidad <c>ConcesionAdministrador</c> (decisión del 2026-10-30; ADR-011
/// § 8.7, punto 3): <c>AutorizacionEscrituraBehavior</c> deja pasar a esa sesión cualquier comando
/// que la implemente. Un segundo comando marcado ampliaría en silencio lo que un Actor de
/// Plataforma TALVEG puede hacer en un Tenant. Mismo ratchet que
/// <see cref="MarcadorDeRevocacionOperadorCaeExternoTests"/>.
/// </summary>
public class MarcadorDeConcesionAdministradorTests
{
    [Fact]
    public void Solo_ConcederAdministradorPorPlataformaCommand_implementa_el_marcador()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        var marcados = application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IComandoDeConcesionAdministrador).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .OrderBy(x => x)
            .ToList();

        marcados.Should().Equal(
            ["CaeManager.Application.Plataforma.Commands.ConcederAdministradorPorPlataforma.ConcederAdministradorPorPlataformaCommand"],
            "cada comando marcado es algo que un Actor de Plataforma TALVEG puede escribir en un Tenant con esta capacidad; " +
            "añadir uno es ampliar la capacidad, y eso se decide y se documenta");
    }
}

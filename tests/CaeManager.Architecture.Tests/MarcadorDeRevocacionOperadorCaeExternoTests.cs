using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>IComandoDeRevocacionOperadorCaeExterno</c> es la única puerta de escritura de una Sesión
/// Privilegiada con la capacidad <c>RevocacionOperadorCaeExterno</c> (enmienda de ADR-004 § 12.2
/// del 2026-10-26; ADR-011 § 8.7, punto 3): <c>AutorizacionEscrituraBehavior</c> deja pasar a esa
/// sesión cualquier comando que la implemente. Un segundo comando marcado ampliaría en silencio
/// lo que Soporte TALVEG puede hacer en un Tenant. Mismo ratchet que
/// <see cref="MarcadorDeRestablecimientoSegundoFactorTests"/>.
/// </summary>
public class MarcadorDeRevocacionOperadorCaeExternoTests
{
    [Fact]
    public void Solo_RevocarOperadorCaeExternoPorSoporteCommand_implementa_el_marcador()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        var marcados = application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IComandoDeRevocacionOperadorCaeExterno).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .OrderBy(x => x)
            .ToList();

        marcados.Should().Equal(
            ["CaeManager.Application.Plataforma.Commands.RevocarOperadorCaeExternoPorSoporte.RevocarOperadorCaeExternoPorSoporteCommand"],
            "cada comando marcado es algo que Soporte TALVEG puede escribir en un Tenant con esta capacidad; " +
            "añadir uno es ampliar la capacidad, y eso se decide y se documenta");
    }
}

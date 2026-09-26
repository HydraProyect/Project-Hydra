using CaeManager.Application.Common;
using CaeManager.Architecture.Tests.Soporte;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <c>IComandoDeRestablecimientoSegundoFactor</c> es la única puerta de escritura
/// de una Sesión Privilegiada con la capacidad <c>RestablecimientoSegundoFactor</c>
/// (ADR-011 § 8.7, punto 3): <c>AutorizacionEscrituraBehavior</c> deja pasar a esa sesión
/// cualquier comando que la implemente. Un segundo comando marcado ampliaría en
/// silencio lo que Soporte TALVEG puede hacer en un Tenant, sin concesión nueva y
/// sin que ningún test de la capacidad se pusiera en rojo. Este ratchet obliga a
/// que ampliarla sea una decisión explícita.
/// </summary>
public class MarcadorDeRestablecimientoSegundoFactorTests
{
    [Fact]
    public void Solo_RestablecerSegundoFactorCommand_implementa_el_marcador()
    {
        var application = ReflexionArquitecturaHelper.CargarAssembly("CaeManager.Application");

        var marcados = application.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false })
            .Where(t => typeof(IComandoDeRestablecimientoSegundoFactor).IsAssignableFrom(t))
            .Select(t => t.FullName!)
            .OrderBy(x => x)
            .ToList();

        marcados.Should().Equal(
            ["CaeManager.Application.Usuarios.Commands.RestablecerSegundoFactor.RestablecerSegundoFactorCommand"],
            "cada comando marcado es algo que Soporte TALVEG puede escribir en un Tenant con esta capacidad; " +
            "añadir uno es ampliar la capacidad, y eso se decide y se documenta en ADR-011 § 8.7, punto 3");
    }
}

using CaeManager.Application.ApiKeys.Commands.RevocarClaveApiDeMiOrganizacion;
using CaeManager.Application.Common;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <see cref="IComandoQueReduceExposicion"/> salta el gate comercial del Tenant, así que su lista es
/// cerrada: un comando nuevo con el marcador se decide aquí, y quitarlo del que revoca claves dejaría
/// a un Tenant suspendido sin poder retirar las suyas.
/// </summary>
public class ComandosQueReducenExposicionTests
{
    [Fact]
    public void Solo_estos_comandos_saltan_el_gate_comercial()
    {
        var marcados = typeof(IComandoQueReduceExposicion).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsAssignableTo(typeof(IComandoQueReduceExposicion)));

        marcados.Should().BeEquivalentTo([typeof(RevocarClaveApiDeMiOrganizacionCommand)]);
    }
}

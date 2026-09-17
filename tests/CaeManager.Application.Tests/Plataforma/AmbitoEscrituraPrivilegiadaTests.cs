using CaeManager.Application.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Plataforma;

/// <summary>
/// PD-A3, commit 4: calcado de los tests de <c>AmbitoTenantExplicito</c> —
/// verifica el contrato del <c>AsyncLocal</c> en sí, sin ninguna dependencia
/// de infraestructura: fluye dentro del <c>using</c>, se restaura al salir, y
/// admite anidamiento correcto (la propiedad crítica para que un handler que
/// invoque a otro no deje el ámbito exterior con el valor del interior).
/// </summary>
public class AmbitoEscrituraPrivilegiadaTests
{
    [Fact]
    public void Sin_ambito_establecido_Actual_es_null()
    {
        AmbitoEscrituraPrivilegiada.Actual.Should().BeNull();
    }

    [Fact]
    public void Dentro_del_using_Actual_devuelve_lo_establecido()
    {
        var sesionId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        using (AmbitoEscrituraPrivilegiada.Establecer(sesionId, tenantId))
        {
            AmbitoEscrituraPrivilegiada.Actual.Should().Be((sesionId, tenantId));
        }
    }

    [Fact]
    public void Al_disponer_el_using_Actual_vuelve_a_null()
    {
        using (AmbitoEscrituraPrivilegiada.Establecer(Guid.NewGuid(), Guid.NewGuid()))
        {
        }

        AmbitoEscrituraPrivilegiada.Actual.Should().BeNull();
    }

    [Fact]
    public void Un_ambito_anidado_restaura_el_exterior_al_disponerse()
    {
        var exterior = (Guid.NewGuid(), Guid.NewGuid());

        using (AmbitoEscrituraPrivilegiada.Establecer(exterior.Item1, exterior.Item2))
        {
            using (AmbitoEscrituraPrivilegiada.Establecer(Guid.NewGuid(), Guid.NewGuid()))
            {
                AmbitoEscrituraPrivilegiada.Actual.Should().NotBe(exterior);
            }

            AmbitoEscrituraPrivilegiada.Actual.Should().Be(exterior,
                "disponer el ámbito interior tiene que devolver exactamente el valor exterior, no null");
        }
    }

    [Fact]
    public async Task El_ambito_fluye_a_traves_de_await_anidados()
    {
        var sesionId = Guid.NewGuid();
        var tenantId = Guid.NewGuid();

        using (AmbitoEscrituraPrivilegiada.Establecer(sesionId, tenantId))
        {
            await Task.Yield();
            await Task.Delay(1);

            AmbitoEscrituraPrivilegiada.Actual.Should().Be((sesionId, tenantId),
                "AsyncLocal tiene que sobrevivir a la reanudación tras un await, igual que AmbitoTenantExplicito");
        }
    }
}

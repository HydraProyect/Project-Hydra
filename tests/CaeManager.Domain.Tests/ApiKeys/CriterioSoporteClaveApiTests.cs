using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.ApiKeys;

public class CriterioSoporteClaveApiTests
{
    // Emitir exige la delegación de Soporte activa y vigente; autenticar, solo que exista entre el
    // mismo origen y el mismo Tenant propietario.
    [Theory]
    [InlineData("vigente", true, true)]
    [InlineData("inactiva", false, true)]
    [InlineData("retirada", false, true)]
    [InlineData("vencida", false, true)]
    [InlineData("otro origen", false, false)]
    [InlineData("otro propietario", false, false)]
    [InlineData("comercial", false, false)]
    public void Emitir_exige_ventana_vigente_y_autenticar_solo_que_exista_la_delegacion(
        string caso, bool puedeEmitir, bool puedeAutenticar)
    {
        var origen = Guid.NewGuid();
        var propietario = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var delegacion = caso == "comercial" ? new DelegacionTenant(origen, propietario)
            : DelegacionTenant.ParaSoporte(caso == "otro origen" ? Guid.NewGuid() : origen,
                caso == "otro propietario" ? Guid.NewGuid() : propietario);
        if (caso != "comercial" && caso != "inactiva")
            delegacion.ActivarParaSoporte("Prueba", caso == "vencida" ? ahora : ahora.AddHours(1), ahora.AddHours(-1));
        if (caso == "retirada") delegacion.Desactivar();

        CriterioSoporteClaveApi.PuedeEmitir(delegacion, origen, propietario, ahora).Should().Be(puedeEmitir);
        CriterioSoporteClaveApi.PuedeAutenticar(origen, propietario).Compile()(delegacion).Should().Be(puedeAutenticar);
        if (caso == "vigente") delegacion.EstaVigente(ahora).Should().BeTrue();
        if (caso == "vencida") delegacion.EstaVigente(ahora).Should().BeFalse();
    }
}

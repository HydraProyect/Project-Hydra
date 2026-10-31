using CaeManager.Domain.ApiKeys;
using CaeManager.Domain.Tenants;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.ApiKeys;

public class CriterioSoporteClaveApiTests
{
    // Emitir exige la delegación de Soporte activa y vigente; autenticar, además, que siga abierta
    // la misma ventana en que se emitió la clave.
    [Theory]
    [InlineData("vigente", true, true)]
    [InlineData("emitida al abrir", true, true)]
    [InlineData("inactiva", false, false)]
    [InlineData("retirada", false, false)]
    [InlineData("vencida", false, false)]
    [InlineData("reabierta tras emitir", true, false)]
    [InlineData("otro origen", false, false)]
    [InlineData("otro propietario", false, false)]
    [InlineData("comercial", false, false)]
    public void Emitir_exige_ventana_vigente_y_autenticar_la_misma_ventana_abierta(
        string caso, bool puedeEmitir, bool puedeAutenticar)
    {
        var origen = Guid.NewGuid();
        var propietario = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var apertura = caso == "reabierta tras emitir" ? ahora.AddMinutes(-5) : ahora.AddHours(-1);
        var emitidaEnUtc = caso == "emitida al abrir" ? apertura : ahora.AddMinutes(-30);
        var delegacion = caso == "comercial" ? new DelegacionTenant(origen, propietario)
            : DelegacionTenant.ParaSoporte(caso == "otro origen" ? Guid.NewGuid() : origen,
                caso == "otro propietario" ? Guid.NewGuid() : propietario);
        if (caso != "comercial" && caso != "inactiva")
            delegacion.ActivarParaSoporte("Prueba", caso == "vencida" ? ahora : ahora.AddHours(1), apertura);
        if (caso == "retirada") delegacion.Desactivar();

        CriterioSoporteClaveApi.PuedeEmitir(delegacion, origen, propietario, ahora).Should().Be(puedeEmitir);
        CriterioSoporteClaveApi.PuedeAutenticar(origen, propietario, emitidaEnUtc, ahora).Compile()(delegacion).Should().Be(puedeAutenticar);
        if (caso == "vigente") delegacion.EstaVigente(ahora).Should().BeTrue();
        if (caso == "vencida") delegacion.EstaVigente(ahora).Should().BeFalse();
    }
}

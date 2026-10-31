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
    [InlineData("otra ventana abierta", true, false)]
    [InlineData("inactiva", false, false)]
    [InlineData("retirada", false, false)]
    [InlineData("vencida", false, false)]
    [InlineData("reabierta tras emitir", true, false)]
    // El origen se comprueba al emitir: después manda la ventana que la clave guarda.
    [InlineData("otro origen", false, true)]
    [InlineData("otro propietario", false, false)]
    [InlineData("comercial", false, false)]
    public void Emitir_exige_ventana_vigente_y_autenticar_la_misma_ventana_abierta(
        string caso, bool puedeEmitir, bool puedeAutenticar)
    {
        var origen = Guid.NewGuid();
        var propietario = Guid.NewGuid();
        var ahora = DateTime.UtcNow;
        var apertura = ahora.AddHours(-1);
        var delegacion = caso == "comercial" ? new DelegacionTenant(origen, propietario)
            : DelegacionTenant.ParaSoporte(caso == "otro origen" ? Guid.NewGuid() : origen,
                caso == "otro propietario" ? Guid.NewGuid() : propietario);
        if (caso != "comercial" && caso != "inactiva")
            delegacion.ActivarParaSoporte("Prueba", caso == "vencida" ? ahora : ahora.AddHours(1), apertura);
        if (caso == "retirada") delegacion.Desactivar();

        CriterioSoporteClaveApi.PuedeEmitir(delegacion, origen, propietario, ahora).Should().Be(puedeEmitir);
        var ventanaDeLaClave = caso == "otra ventana abierta" ? Guid.NewGuid() : delegacion.Id;
        var aperturaDeLaClave = caso == "reabierta tras emitir" ? apertura.AddMinutes(-30) : apertura;
        CriterioSoporteClaveApi.PuedeAutenticar(ventanaDeLaClave, aperturaDeLaClave, propietario, ahora).Compile()(delegacion).Should().Be(puedeAutenticar);
        if (caso == "vigente") delegacion.EstaVigente(ahora).Should().BeTrue();
        if (caso == "vencida") delegacion.EstaVigente(ahora).Should().BeFalse();
    }
}

using CaeManager.Infrastructure.Importacion;
using FluentAssertions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace CaeManager.Web.Tests;

/// <summary>M-43: la plantilla CAE completa sin hojas de Trabajadores es visible en <c>/salud</c> sin impedir servir.</summary>
public class PlantillaCaeCompletaSaludTests
{
    private static Task<HealthCheckResult> Comprobar(OpcionesPlantillaCaeCompleta opciones) =>
        new PlantillaCaeCompletaHealthCheck(new PlantillaCaeCompletaConfigurada(Options.Create(opciones))).CheckHealthAsync(new HealthCheckContext());

    [Fact]
    public async Task Sin_hojas_configuradas_es_Degraded_y_nunca_Unhealthy()
    {
        var resultado = await Comprobar(new OpcionesPlantillaCaeCompleta());
        resultado.Status.Should().Be(HealthStatus.Degraded, "no puede impedir el relevo ni el arranque");
    }

    [Fact]
    public async Task Una_entrada_a_medias_cuenta_como_sin_configurar()
    {
        var opciones = new OpcionesPlantillaCaeCompleta { HojasTrabajadores = [new HojaTrabajadoresCae { Hoja = "Plantilla", RazonSocialEmpresa = " " }] };
        (await Comprobar(opciones)).Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Con_una_hoja_completa_es_Healthy()
    {
        var opciones = new OpcionesPlantillaCaeCompleta { HojasTrabajadores = [new HojaTrabajadoresCae { Hoja = "Plantilla", RazonSocialEmpresa = "Empresa Demo S.L." }] };
        (await Comprobar(opciones)).Status.Should().Be(HealthStatus.Healthy);
    }
}

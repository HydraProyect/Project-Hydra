using CaeManager.Application.DocumentosIa;
using CaeManager.Application.DocumentosIa.Common;
using CaeManager.Domain.Common;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.DocumentosIa;

public class RouterExtraccionMetadatosDocumentoIaServiceTests
{
    [Fact]
    public async Task Parsea_fecha_emision_fecha_vencimiento_y_firma_de_los_campos_libres()
    {
        var campos = new Dictionary<string, string?>
        {
            ["fechaEmision"] = "2026-01-15",
            ["fechaVencimiento"] = "2027-01-15",
            ["tieneFirma"] = "true",
            ["aseguradora"] = "Mapfre"
        };
        var router = new DocumentAIRouterServiceFalso(
            Result.Exito(new ExtraccionEstructuradaDto("Póliza de seguro", campos, 95, null)));
        var servicio = new RouterExtraccionMetadatosDocumentoIaService(router);

        var resultado = await servicio.ExtraerAsync([1, 2, 3], "Reconocimiento médico");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.TipoDetectado.Should().Be("Póliza de seguro");
        resultado.Valor.FechaEmisionDetectada.Should().Be(new DateOnly(2026, 1, 15));
        resultado.Valor.FechaVencimientoDetectada.Should().Be(new DateOnly(2027, 1, 15));
        resultado.Valor.TieneFirma.Should().BeTrue();
        resultado.Valor.ConfianzaGeneral.Should().Be(95);
    }

    [Fact]
    public async Task Devuelve_null_de_forma_tolerante_cuando_faltan_o_no_se_pueden_interpretar_los_campos()
    {
        var campos = new Dictionary<string, string?> { ["tieneFirma"] = "no-es-un-booleano" };
        var router = new DocumentAIRouterServiceFalso(
            Result.Exito(new ExtraccionEstructuradaDto(null, campos, 40, "documento borroso")));
        var servicio = new RouterExtraccionMetadatosDocumentoIaService(router);

        var resultado = await servicio.ExtraerAsync([1, 2, 3], "Apto médico");

        resultado.EsExitoso.Should().BeTrue();
        resultado.Valor.FechaEmisionDetectada.Should().BeNull();
        resultado.Valor.FechaVencimientoDetectada.Should().BeNull();
        resultado.Valor.TieneFirma.Should().BeNull();
    }

    [Fact]
    public async Task Propaga_el_fallo_del_router_sin_modificarlo()
    {
        var router = new DocumentAIRouterServiceFalso(
            Result.Fallo<ExtraccionEstructuradaDto>(Error.Crear("DocumentAIProvider.NoConfigurado", "no disponible")));
        var servicio = new RouterExtraccionMetadatosDocumentoIaService(router);

        var resultado = await servicio.ExtraerAsync([1, 2, 3], "Apto médico");

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("DocumentAIProvider.NoConfigurado");
    }
}

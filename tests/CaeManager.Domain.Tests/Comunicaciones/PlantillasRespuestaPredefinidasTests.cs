using CaeManager.Domain.Comunicaciones;
using FluentAssertions;

namespace CaeManager.Domain.Tests.Comunicaciones;

public class PlantillasRespuestaPredefinidasTests
{
    [Fact]
    public void Hay_nueve_plantillas_editables_con_identidades_estables_por_Tenant_propietario()
    {
        var tenantId = Guid.NewGuid();
        var plantillas = PlantillasRespuestaPredefinidas.Crear(tenantId);
        plantillas.Should().HaveCount(9).And.OnlyHaveUniqueItems(p => p.Id);
        plantillas.Select(p => p.Id).Should().Equal(PlantillasRespuestaPredefinidas.Crear(tenantId).Select(p => p.Id));
        plantillas.Select(p => p.Id).Should().NotIntersectWith(PlantillasRespuestaPredefinidas.Crear(Guid.NewGuid()).Select(p => p.Id));
        foreach (var plantilla in plantillas)
        {
            plantilla.ClienteId.Should().BeNull();
            plantilla.CuerpoHtml.Should().Contain("{{Empresa}}").And.Contain("{{Centro}}");
            plantilla.Actualizar("Texto del equipo", "Contenido revisado", null);
            plantilla.Titulo.Should().Be("Texto del equipo");
            plantilla.CuerpoHtml.Should().Be("Contenido revisado");
        }
    }

    [Fact]
    public void No_se_puede_sembrar_sin_Tenant_propietario() =>
        FluentActions.Invoking(() => PlantillasRespuestaPredefinidas.Crear(Guid.Empty)).Should().Throw<ArgumentException>();
}

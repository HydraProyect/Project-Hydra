using System.Globalization;
using System.Resources;
using CaeManager.Application.AsistenteIa.Ordenes;
using CaeManager.Web.Features.AsistenteIa.Recursos;
using FluentAssertions;

namespace CaeManager.Web.Tests;

/// <summary>
/// El plan del agente rotula órdenes, datos y pasos con claves derivadas del catálogo
/// (<c>Orden_</c>, <c>Campo_</c>, <c>Paso_</c>). Una orden, un campo o una operación nueva
/// en el catálogo sin su texto dejaría la interfaz mostrando el nombre de la clave: este
/// test lo detecta en español y en catalán antes de que llegue a pantalla.
/// </summary>
public class TextosDelPlanAsistenteTests
{
    private static readonly ResourceManager Recursos = new(typeof(TextosAsistenteIa).FullName!, typeof(TextosAsistenteIa).Assembly);

    public static TheoryData<string> Culturas => ["es", "ca-ES"];

    private static IEnumerable<string> ClavesDelCatalogo() =>
        CatalogoOrdenesAsistente.Ordenes.SelectMany(o => new[] { $"Orden_{o.Id}" }
            .Concat(o.Campos.Select(c => $"Campo_{c.Nombre}"))
            .Concat(o.Ejecucion.Select(p => $"Paso_{p.Operacion.Name}")))
            .Distinct();

    [Theory]
    [MemberData(nameof(Culturas))]
    public void Cada_orden_campo_y_paso_del_catalogo_tiene_texto(string cultura)
    {
        var faltan = ClavesDelCatalogo()
            .Where(k => string.IsNullOrWhiteSpace(Recursos.GetString(k, CultureInfo.GetCultureInfo(cultura))))
            .ToList();

        faltan.Should().BeEmpty();
        ClavesDelCatalogo().Should().HaveCountGreaterThan(20, "el barrido tiene que ver el catálogo entero");
    }

    [Fact]
    public void Las_claves_del_plan_no_traducidas_al_catalan_no_son_copia_del_castellano_en_los_titulos()
    {
        // Control positivo: si la cultura catalana cayera al recurso neutral, este test lo vería.
        Recursos.GetString("Orden_alta_centro", CultureInfo.GetCultureInfo("ca-ES"))
            .Should().NotBe(Recursos.GetString("Orden_alta_centro", CultureInfo.GetCultureInfo("es")));
    }
}

using CaeManager.Domain.Subcontratas;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Subcontratas;

public class SubcontrataTests
{
    [Fact]
    public void Crea_una_subcontrata_valida()
    {
        var subcontrata = new Subcontrata("Limpiezas Ejemplo S.L.");

        subcontrata.RazonSocial.Should().Be("Limpiezas Ejemplo S.L.");
    }

    [Fact]
    public void Recorta_espacios_en_la_razon_social()
    {
        var subcontrata = new Subcontrata("  Limpiezas Ejemplo S.L.  ");

        subcontrata.RazonSocial.Should().Be("Limpiezas Ejemplo S.L.");
    }

    [Fact]
    public void Rechaza_una_razon_social_vacia()
    {
        var accion = () => new Subcontrata("   ");

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Actualizar_cambia_la_razon_social()
    {
        var subcontrata = new Subcontrata("Nombre original");

        subcontrata.Actualizar("Nombre nuevo");

        subcontrata.RazonSocial.Should().Be("Nombre nuevo");
    }
}

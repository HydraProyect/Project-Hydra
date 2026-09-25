using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

/// <summary>P1-X2: variante de Centro sin gestión CAE.</summary>
public class CentroGestionCaeTests
{
    [Fact]
    public void Un_centro_nuevo_requiere_gestion_cae()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Planta Zaragoza");

        centro.GestionCae.Should().Be(ModalidadGestionCae.ConGestionCae);
        centro.RequiereGestionCae.Should().BeTrue();
    }

    [Fact]
    public void Marcar_sin_gestion_cae_deja_de_requerirla()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Oficina Huesca");

        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        centro.GestionCae.Should().Be(ModalidadGestionCae.SinGestionCae);
        centro.RequiereGestionCae.Should().BeFalse();
    }

    [Fact]
    public void Volver_a_con_gestion_cae_la_requiere_otra_vez()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Oficina Huesca");
        centro.EstablecerGestionCae(ModalidadGestionCae.SinGestionCae);

        centro.EstablecerGestionCae(ModalidadGestionCae.ConGestionCae);

        centro.RequiereGestionCae.Should().BeTrue();
    }

    [Fact]
    public void Rechaza_una_modalidad_desconocida()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Oficina Huesca");

        var accion = () => centro.EstablecerGestionCae((ModalidadGestionCae)7);

        accion.Should().Throw<ArgumentOutOfRangeException>();
        centro.GestionCae.Should().Be(ModalidadGestionCae.ConGestionCae);
    }

    [Fact]
    public void Los_valores_persistidos_son_estables()
    {
        ((int)ModalidadGestionCae.ConGestionCae).Should().Be(0);
        ((int)ModalidadGestionCae.SinGestionCae).Should().Be(1);
    }
}

using CaeManager.Domain.Centros;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Centros;

public class FichaCentroTests
{
    [Fact]
    public void La_ficha_normaliza_los_contactos_y_conserva_los_tres_trabajos_por_separado()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba");
        centro.ActualizarFicha(" 7 a 19 ", " Accesos ", " 100 ", " Prevención ", " 200 ", " prl@example.test ", true, false, null);
        centro.HorarioAcceso.Should().Be("7 a 19");
        centro.ControlAccesosNombre.Should().Be("Accesos");
        centro.ControlAccesosTelefono.Should().Be("100");
        centro.TecnicoPrevencionNombre.Should().Be("Prevención");
        centro.TecnicoPrevencionTelefono.Should().Be("200");
        centro.TecnicoPrevencionCorreo.Should().Be("prl@example.test");
        centro.TrabajosEnAltura.Should().BeTrue();
        centro.EspaciosConfinados.Should().BeFalse();
        centro.AtmosferasExplosivas.Should().BeNull();
    }

    [Fact]
    public void Vaciar_la_ficha_no_modifica_la_identidad_ni_el_Tenant_propietario()
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba", direccion: "Calle de prueba");
        var identidad = (centro.Id, centro.TenantId, centro.ClienteId, centro.EmpresaId);
        centro.ActualizarFicha("Horario", "Nombre", "100", "Técnico", "200", "prl@example.test", true, true, true);
        centro.ActualizarFicha(" ", null, "", null, null, " ", null, null, null);
        centro.HorarioAcceso.Should().BeNull();
        centro.ControlAccesosNombre.Should().BeNull();
        centro.TecnicoPrevencionCorreo.Should().BeNull();
        centro.TrabajosEnAltura.Should().BeNull();
        centro.EspaciosConfinados.Should().BeNull();
        centro.AtmosferasExplosivas.Should().BeNull();
        (centro.Id, centro.TenantId, centro.ClienteId, centro.EmpresaId).Should().Be(identidad);
        centro.Direccion.Should().Be("Calle de prueba");
    }

    [Theory]
    [InlineData(0, 501)]
    [InlineData(1, 201)]
    [InlineData(2, 51)]
    [InlineData(3, 201)]
    [InlineData(4, 51)]
    [InlineData(5, 255)]
    public void Rechaza_longitudes_sin_modificar_parcialmente_la_ficha(int campo, int longitud)
    {
        var centro = new Centro(Guid.NewGuid(), Guid.NewGuid(), "Centro de prueba");
        var valores = new string?[6];
        valores[0] = "Horario nuevo";
        valores[campo] = new string('x', longitud);
        var actualizar = () => centro.ActualizarFicha(valores[0], valores[1], valores[2], valores[3], valores[4], valores[5], true, true, true);
        actualizar.Should().Throw<ArgumentException>();
        centro.HorarioAcceso.Should().BeNull();
        centro.TrabajosEnAltura.Should().BeNull();
    }
}

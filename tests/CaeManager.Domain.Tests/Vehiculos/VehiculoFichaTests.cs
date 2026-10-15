using CaeManager.Domain.Vehiculos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Vehiculos;

public class VehiculoFichaTests
{
    private static Vehiculo Crear() => Vehiculo.DeEmpresa(Guid.NewGuid(), "Camión", "Daily", "9012 GHI");

    [Fact]
    public void Normaliza_los_siete_campos_y_no_toca_la_identidad_ni_la_nota()
    {
        var vehiculo = Crear();
        vehiculo.FijarNotaInterna("Llaves en recepción.");
        vehiculo.ActualizarFicha(" Camión con grúa autocargante ", " Renting ", " Última el 11/11/2025 ",
            " Registrada ", " Nostromo ", " AU-2026 ", " Pagado hasta el 31/12/2026 ");

        new[] { vehiculo.Tipo, vehiculo.Titularidad, vehiculo.RevisionGrua, vehiculo.DeclaracionCe,
            vehiculo.Aseguradora, vehiculo.Poliza, vehiculo.Recibo }.Should().Equal(
            "Camión con grúa autocargante", "Renting", "Última el 11/11/2025", "Registrada", "Nostromo", "AU-2026", "Pagado hasta el 31/12/2026");
        vehiculo.Nombre.Should().Be("Camión");
        vehiculo.Modelo.Should().Be("Daily");
        vehiculo.NumeroPlaca.Should().Be("9012 GHI");
        vehiculo.Notas.Should().Be("Llaves en recepción.");
    }

    [Fact]
    public void Un_campo_invalido_no_modifica_los_anteriores()
    {
        var vehiculo = Crear();
        vehiculo.ActualizarFicha("Grúa", null, null, null, null, null, null);
        var actuar = () => vehiculo.ActualizarFicha("Camión", null, null, null, null, null,
            new string('x', Vehiculo.LongitudMaximaCampoFicha + 1));
        actuar.Should().Throw<ArgumentException>();
        vehiculo.Tipo.Should().Be("Grúa");
        vehiculo.Recibo.Should().BeNull();
    }

    [Fact]
    public void Los_campos_vacios_son_nulos_y_editar_identidad_no_borra_la_ficha()
    {
        var vehiculo = Crear();
        vehiculo.ActualizarFicha(null, " ", "", null, "Nostromo", null, "\t");
        vehiculo.Actualizar("Nuevo nombre", "Transit", "1111 AAA");
        new[] { vehiculo.Tipo, vehiculo.Titularidad, vehiculo.RevisionGrua, vehiculo.DeclaracionCe,
            vehiculo.Poliza, vehiculo.Recibo }.Should().OnlyContain(v => v == null);
        vehiculo.Aseguradora.Should().Be("Nostromo");
    }

    [Fact]
    public void La_relacion_no_duplica_personas_y_renueva_la_version_al_anadir_y_retirar()
    {
        var vehiculo = Crear();
        var trabajadorId = Guid.NewGuid();
        var version = vehiculo.Version;
        vehiculo.AutorizarConductor(trabajadorId);
        vehiculo.Version.Should().NotBe(version);
        version = vehiculo.Version;
        vehiculo.AutorizarConductor(trabajadorId);
        vehiculo.Conductores.Should().ContainSingle().Which.VehiculoId.Should().Be(vehiculo.Id);
        vehiculo.Version.Should().Be(version);
        vehiculo.RetirarConductor(vehiculo.Conductores.Single());
        vehiculo.Conductores.Should().BeEmpty();
        vehiculo.Version.Should().NotBe(version);
    }

    [Fact]
    public void La_relacion_rechaza_identificadores_vacios()
    {
        var sinVehiculo = () => new VehiculoTrabajador(Guid.Empty, Guid.NewGuid());
        var sinTrabajador = () => Crear().AutorizarConductor(Guid.Empty);
        sinVehiculo.Should().Throw<ArgumentException>();
        sinTrabajador.Should().Throw<ArgumentException>();
    }
}

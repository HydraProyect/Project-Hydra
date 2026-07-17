using CaeManager.Domain.Documentos;
using FluentAssertions;
using Xunit;

namespace CaeManager.Domain.Tests.Documentos;

public class DocumentoTests
{
    private static readonly DateOnly Hoy = DateOnly.FromDateTime(DateTime.UtcNow);

    [Fact]
    public void DeTrabajador_asigna_solo_TrabajadorId()
    {
        var trabajadorId = Guid.NewGuid();

        var documento = Documento.DeTrabajador(trabajadorId, Guid.NewGuid(), Hoy, null);

        documento.TrabajadorId.Should().Be(trabajadorId);
        documento.ClienteId.Should().BeNull();
        documento.EmpresaId.Should().BeNull();
        documento.Ambito.Should().Be(AmbitoAplicacion.Trabajador);
    }

    [Fact]
    public void DeCliente_asigna_solo_ClienteId()
    {
        var clienteId = Guid.NewGuid();

        var documento = Documento.DeCliente(clienteId, Guid.NewGuid(), Hoy, null);

        documento.ClienteId.Should().Be(clienteId);
        documento.TrabajadorId.Should().BeNull();
        documento.EmpresaId.Should().BeNull();
        documento.Ambito.Should().Be(AmbitoAplicacion.Cliente);
    }

    [Fact]
    public void DeEmpresa_asigna_solo_EmpresaId()
    {
        var empresaId = Guid.NewGuid();

        var documento = Documento.DeEmpresa(empresaId, Guid.NewGuid(), Hoy, null);

        documento.EmpresaId.Should().Be(empresaId);
        documento.TrabajadorId.Should().BeNull();
        documento.ClienteId.Should().BeNull();
        documento.Ambito.Should().Be(AmbitoAplicacion.Empresa);
    }

    [Fact]
    public void DeVehiculo_asigna_solo_VehiculoId()
    {
        var vehiculoId = Guid.NewGuid();

        var documento = Documento.DeVehiculo(vehiculoId, Guid.NewGuid(), Hoy, null);

        documento.VehiculoId.Should().Be(vehiculoId);
        documento.TrabajadorId.Should().BeNull();
        documento.ClienteId.Should().BeNull();
        documento.EmpresaId.Should().BeNull();
        documento.Ambito.Should().Be(AmbitoAplicacion.Vehiculo);
    }

    [Fact]
    public void No_permite_crear_un_documento_de_vehiculo_sin_vehiculo()
    {
        var accion = () => Documento.DeVehiculo(Guid.Empty, Guid.NewGuid(), Hoy, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_crear_un_documento_de_trabajador_sin_trabajador()
    {
        var accion = () => Documento.DeTrabajador(Guid.Empty, Guid.NewGuid(), Hoy, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_crear_un_documento_de_cliente_sin_cliente()
    {
        var accion = () => Documento.DeCliente(Guid.Empty, Guid.NewGuid(), Hoy, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_crear_un_documento_de_empresa_sin_empresa()
    {
        var accion = () => Documento.DeEmpresa(Guid.Empty, Guid.NewGuid(), Hoy, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_un_tipo_de_documento_vacio()
    {
        var accion = () => Documento.DeCliente(Guid.NewGuid(), Guid.Empty, Hoy, null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void No_permite_una_fecha_de_emision_futura()
    {
        var accion = () => Documento.DeEmpresa(Guid.NewGuid(), Guid.NewGuid(), Hoy.AddDays(1), null);

        accion.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Renovar_no_cambia_el_propietario()
    {
        var clienteId = Guid.NewGuid();
        var documento = Documento.DeCliente(clienteId, Guid.NewGuid(), Hoy.AddDays(-30), null);

        documento.Renovar(Hoy, Hoy.AddYears(1));

        documento.ClienteId.Should().Be(clienteId);
        documento.Ambito.Should().Be(AmbitoAplicacion.Cliente);
        documento.FechaVencimiento.Should().Be(Hoy.AddYears(1));
    }
}

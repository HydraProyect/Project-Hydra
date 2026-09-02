using System.Reflection;
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
    public void DeProyecto_asigna_solo_ProyectoId()
    {
        var proyectoId = Guid.NewGuid();

        var documento = Documento.DeProyecto(proyectoId, Guid.NewGuid(), Hoy, null);

        documento.ProyectoId.Should().Be(proyectoId);
        documento.TrabajadorId.Should().BeNull();
        documento.ClienteId.Should().BeNull();
        documento.EmpresaId.Should().BeNull();
        documento.VehiculoId.Should().BeNull();
        documento.Ambito.Should().Be(AmbitoAplicacion.Proyecto);
    }

    [Fact]
    public void No_permite_crear_un_documento_de_proyecto_sin_proyecto()
    {
        var accion = () => Documento.DeProyecto(Guid.Empty, Guid.NewGuid(), Hoy, null);

        accion.Should().Throw<ArgumentException>();
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

    // DCR-19: las cinco factorías (DeTrabajador/DeCliente/DeEmpresa/DeVehiculo/
    // DeProyecto) siempre pasan exactamente un propietario — ningún camino
    // público llega nunca al constructor con cero o dos, así que estos dos
    // tests invocan el constructor privado con parámetros por reflexión, el
    // único camino honesto para ejercer su guarda directamente.
    [Fact]
    public void El_constructor_privado_rechaza_un_documento_sin_ningun_propietario()
    {
        var accion = () => InvocarConstructorConParametros(null, null, null, null, null);

        accion.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentException>()
            .WithMessage("*exactamente un propietario*");
    }

    [Fact]
    public void El_constructor_privado_rechaza_un_documento_con_dos_propietarios()
    {
        var accion = () => InvocarConstructorConParametros(Guid.NewGuid(), Guid.NewGuid(), null, null, null);

        accion.Should().Throw<TargetInvocationException>()
            .WithInnerException<ArgumentException>()
            .WithMessage("*exactamente un propietario*");
    }

    // DCR-19 / riesgo 3 del handoff: confirmado por inspección del
    // ConstructorBinding del modelo EF que la materialización usa
    // Documento() sin parámetros, no el privado con parámetros — así que
    // este es el mismo camino que reproduce una fila inválida ya
    // materializada, y es lo que Ambito (no el constructor) tiene que
    // rechazar.
    [Fact]
    public void Ambito_lanza_si_el_documento_no_tiene_ningun_propietario()
    {
        var documento = InvocarConstructorSinParametros();

        var accion = () => documento.Ambito;

        accion.Should().Throw<InvalidOperationException>()
            .WithMessage("*sin propietario*");
    }

    private static object InvocarConstructorConParametros(
        Guid? trabajadorId, Guid? clienteId, Guid? empresaId, Guid? vehiculoId, Guid? proyectoId)
    {
        var constructor = typeof(Documento)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 10);

        return constructor.Invoke(
        [
            trabajadorId, clienteId, empresaId, vehiculoId, proyectoId,
            Guid.NewGuid(), Hoy, null, null, null
        ]);
    }

    private static Documento InvocarConstructorSinParametros()
    {
        var constructor = typeof(Documento)
            .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance)
            .Single(c => c.GetParameters().Length == 0);

        return (Documento)constructor.Invoke(null);
    }
}

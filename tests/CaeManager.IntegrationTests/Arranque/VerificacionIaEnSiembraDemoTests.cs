using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Persistence.Seed;
using FluentAssertions;
using Microsoft.Extensions.Configuration;

namespace CaeManager.IntegrationTests.Arranque;

/// <summary>
/// La siembra de demo enciende la verificación IA SOLO si hay un proveedor
/// configurado de verdad (decisión del propietario, 2026-08-28, opción B).
///
/// <para>Sin clave, el comportamiento tiene que ser <b>idéntico</b> al de antes:
/// esa es la mitad del contrato que se rompería sin darse cuenta, porque un
/// "activa de más" no falla — simplemente hace llamadas de pago que nadie pidió
/// sobre tenants de demo en producción.</para>
///
/// <para>Sin base de datos a propósito: las dos funciones bajo prueba son puras,
/// y montar un contexto para ejercitarlas convertiría un test de dos milisegundos
/// en uno de treinta segundos sin observar nada más.</para>
/// </summary>
public class VerificacionIaEnSiembraDemoTests
{
    private static IConfiguration ConfiguracionCon(string? apiKey) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(apiKey is null
                ? []
                : new Dictionary<string, string?> { [DatosPruebaSeeder.ClaveApiProveedorIa] = apiKey })
            .Build();

    private static TipoDocumento Tipo(string nombre, AmbitoAplicacion ambito) =>
        new(nombre, vigenciaMeses: 12, aplicaVencimientoAutomatico: true, orden: 1, ambitoAplicacion: ambito);

    // --- La decisión: ¿hay proveedor? ---

    [Fact]
    public void Sin_la_clave_definida_no_hay_proveedor()
        => DatosPruebaSeeder.HayProveedorIaConfigurado(ConfiguracionCon(null)).Should().BeFalse();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Una_clave_vacia_o_en_blanco_no_cuenta_como_proveedor(string valor)
        => DatosPruebaSeeder.HayProveedorIaConfigurado(ConfiguracionCon(valor)).Should().BeFalse();

    [Fact]
    public void Con_clave_configurada_hay_proveedor()
        => DatosPruebaSeeder.HayProveedorIaConfigurado(ConfiguracionCon("sk-ant-loquesea")).Should().BeTrue();

    // --- La aplicación: a quién enciende ---

    [Fact]
    public void Enciende_los_tipos_de_Trabajador()
    {
        var tipos = new List<TipoDocumento>
        {
            Tipo("Reconocimiento médico", AmbitoAplicacion.Trabajador),
            Tipo("Formación PRL", AmbitoAplicacion.Trabajador),
        };

        DatosPruebaSeeder.ActivarVerificacionIaEnTiposDeTrabajador(tipos).Should().Be(2);
        tipos.Should().OnlyContain(t => t.VerificacionIaActiva);
    }

    /// <summary>
    /// Es la misma restricción que impone <c>ActualizarVerificacionIaGlobalCommand</c>,
    /// que rechaza cualquier ámbito distinto de Trabajador. Sembrar más amplio
    /// crearía un estado que ninguna pantalla podría deshacer.
    /// </summary>
    [Theory]
    [InlineData(AmbitoAplicacion.Empresa)]
    [InlineData(AmbitoAplicacion.Vehiculo)]
    public void No_toca_los_ambitos_que_el_producto_rechaza(AmbitoAplicacion ambito)
    {
        var tipos = new List<TipoDocumento> { Tipo("Un tipo", ambito) };

        DatosPruebaSeeder.ActivarVerificacionIaEnTiposDeTrabajador(tipos).Should().Be(0);
        tipos[0].VerificacionIaActiva.Should().BeFalse();
    }

    [Fact]
    public void No_enciende_un_tipo_con_la_lectura_IA_apagada()
    {
        var tipo = Tipo("Sin lectura IA", AmbitoAplicacion.Trabajador);
        tipo.EstablecerLecturaIaActiva(false);

        DatosPruebaSeeder.ActivarVerificacionIaEnTiposDeTrabajador([tipo]).Should().Be(0);
        tipo.VerificacionIaActiva.Should().BeFalse();
    }

    /// <summary>
    /// Idempotencia: la siembra puede reejecutarse, y un recuento que incluyera
    /// los ya activos mentiría en el log sobre cuántos cambió de verdad.
    /// </summary>
    [Fact]
    public void Es_idempotente_y_no_cuenta_los_que_ya_estaban_activos()
    {
        var tipos = new List<TipoDocumento> { Tipo("Reconocimiento médico", AmbitoAplicacion.Trabajador) };

        DatosPruebaSeeder.ActivarVerificacionIaEnTiposDeTrabajador(tipos).Should().Be(1);
        DatosPruebaSeeder.ActivarVerificacionIaEnTiposDeTrabajador(tipos).Should().Be(0);
        tipos[0].VerificacionIaActiva.Should().BeTrue();
    }

    [Fact]
    public void Mezcla_realista_solo_enciende_los_de_Trabajador_con_lectura_activa()
    {
        var apagado = Tipo("Trabajador sin lectura", AmbitoAplicacion.Trabajador);
        apagado.EstablecerLecturaIaActiva(false);

        var tipos = new List<TipoDocumento>
        {
            Tipo("Trabajador A", AmbitoAplicacion.Trabajador),
            Tipo("Trabajador B", AmbitoAplicacion.Trabajador),
            apagado,
            Tipo("Empresa", AmbitoAplicacion.Empresa),
            Tipo("Vehículo", AmbitoAplicacion.Vehiculo),
        };

        DatosPruebaSeeder.ActivarVerificacionIaEnTiposDeTrabajador(tipos).Should().Be(2);
        tipos.Count(t => t.VerificacionIaActiva).Should().Be(2);
    }
}

using CaeManager.Web.Features.Integraciones;
using FluentAssertions;
using Xunit;

namespace CaeManager.Web.Tests.Integraciones;

/// <summary>
/// La elección de propietario que comparten Conexiones y el cajón «Añadir buzón» de Buzones: qué URL de conectar sale de cada
/// elección y qué elecciones se excluyen. Quien autoriza y valida sigue siendo <c>ConectarMicrosoft365Endpoints</c>.
/// </summary>
public class SeleccionPropietarioBuzonTests
{
    private static readonly Guid ClienteId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly Guid GestorId = Guid.Parse("66666666-7777-8888-9999-000000000000");

    [Fact]
    public void Sin_eleccion_la_URL_es_la_ruta_de_conectar_sin_parametros()
    {
        var seleccion = new SeleccionPropietarioBuzon();

        seleccion.UrlConectar.Should().Be("/integraciones/conectar-microsoft365");
        seleccion.HayEleccion.Should().BeFalse();
    }

    [Fact]
    public void Con_una_Empresa_cliente_la_URL_lleva_su_clienteId()
    {
        var seleccion = new SeleccionPropietarioBuzon();

        seleccion.SeleccionarCliente(ClienteId.ToString());

        seleccion.UrlConectar.Should().Be($"/integraciones/conectar-microsoft365?clienteId={ClienteId}");
        seleccion.HayEleccion.Should().BeTrue();
    }

    [Fact]
    public void Con_un_Gestor_CAE_la_URL_lleva_solo_su_gestorPropietarioId()
    {
        var seleccion = new SeleccionPropietarioBuzon();

        seleccion.SeleccionarGestorPropietario(GestorId.ToString());

        seleccion.UrlConectar.Should().Be($"/integraciones/conectar-microsoft365?gestorPropietarioId={GestorId}");
        seleccion.HayEleccion.Should().BeTrue();
    }

    [Fact]
    public void Con_un_compartido_la_URL_lleva_la_direccion_recortada_y_escapada()
    {
        var seleccion = new SeleccionPropietarioBuzon();

        seleccion.FijarBuzonCompartido("  cae+obra@empresa.test ");

        seleccion.UrlConectar.Should().Be("/integraciones/conectar-microsoft365?buzonCompartido=cae%2Bobra%40empresa.test");
        seleccion.HayBuzonCompartido.Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Un_compartido_en_blanco_no_cuenta_como_eleccion(string valor)
    {
        var seleccion = new SeleccionPropietarioBuzon();

        seleccion.FijarBuzonCompartido(valor);

        seleccion.HayEleccion.Should().BeFalse();
        seleccion.UrlConectar.Should().Be("/integraciones/conectar-microsoft365");
    }

    [Fact]
    public void Elegir_un_Gestor_CAE_limpia_la_Empresa_cliente_y_al_reves()
    {
        var seleccion = new SeleccionPropietarioBuzon();
        seleccion.SeleccionarCliente(ClienteId.ToString());

        seleccion.SeleccionarGestorPropietario(GestorId.ToString());

        seleccion.EmpresaSeleccionadaId.Should().BeNull();
        seleccion.GestorPropietarioId.Should().Be(GestorId);

        seleccion.SeleccionarCliente(ClienteId.ToString());

        seleccion.GestorPropietarioId.Should().BeNull();
        seleccion.EmpresaSeleccionadaId.Should().Be(ClienteId);
    }

    [Fact]
    public void Escribir_un_compartido_limpia_el_Gestor_CAE()
    {
        var seleccion = new SeleccionPropietarioBuzon();
        seleccion.SeleccionarGestorPropietario(GestorId.ToString());

        seleccion.FijarBuzonCompartido("cae@empresa.test");

        seleccion.GestorPropietarioId.Should().BeNull();
        seleccion.UrlConectar.Should().Be("/integraciones/conectar-microsoft365?buzonCompartido=cae%40empresa.test");
    }

    /// <summary>
    /// Los campos deshabilitan la combinación, pero la URL no la oculta: Empresa cliente y compartido viajan juntos y el endpoint
    /// la rechaza con «buzon-compartido» sin pedir consentimiento a Microsoft.
    /// </summary>
    [Fact]
    public void Empresa_cliente_y_compartido_viajan_juntos_para_que_el_endpoint_los_rechace()
    {
        var seleccion = new SeleccionPropietarioBuzon();
        seleccion.SeleccionarCliente(ClienteId.ToString());

        seleccion.FijarBuzonCompartido("cae@empresa.test");

        seleccion.UrlConectar.Should().Be(
            $"/integraciones/conectar-microsoft365?clienteId={ClienteId}&buzonCompartido=cae%40empresa.test");
    }

    [Theory]
    [InlineData("")]
    [InlineData("no-es-un-guid")]
    public void Un_valor_que_no_es_un_Guid_deselecciona(string valor)
    {
        var seleccion = new SeleccionPropietarioBuzon();
        seleccion.SeleccionarCliente(ClienteId.ToString());
        seleccion.SeleccionarGestorPropietario(GestorId.ToString());

        seleccion.SeleccionarGestorPropietario(valor);
        seleccion.SeleccionarCliente(valor);

        seleccion.GestorPropietarioId.Should().BeNull();
        seleccion.EmpresaSeleccionadaId.Should().BeNull();
        seleccion.UrlConectar.Should().Be("/integraciones/conectar-microsoft365");
    }

    [Fact]
    public void Limpiar_deja_la_seleccion_sin_eleccion()
    {
        var seleccion = new SeleccionPropietarioBuzon();
        seleccion.SeleccionarCliente(ClienteId.ToString());
        seleccion.FijarBuzonCompartido("cae@empresa.test");

        seleccion.Limpiar();

        seleccion.HayEleccion.Should().BeFalse();
        seleccion.BuzonCompartido.Should().BeEmpty();
        seleccion.UrlConectar.Should().Be("/integraciones/conectar-microsoft365");
    }
}

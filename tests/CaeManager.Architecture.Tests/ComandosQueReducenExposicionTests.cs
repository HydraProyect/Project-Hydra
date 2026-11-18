using CaeManager.Application.ApiKeys.Commands.RevocarClaveApiDeMiOrganizacion;
using CaeManager.Application.Common;
using FluentAssertions;

namespace CaeManager.Architecture.Tests;

/// <summary>
/// <see cref="IComandoQueReduceExposicion"/> salta el gate comercial del Tenant, así que su lista es
/// cerrada: un comando nuevo con el marcador se decide aquí, y quitarlo del que revoca claves dejaría
/// a un Tenant suspendido sin poder retirar las suyas.
/// </summary>
public class ComandosQueReducenExposicionTests
{
    [Fact]
    public void Solo_estos_comandos_saltan_el_gate_comercial()
    {
        var marcados = typeof(IComandoQueReduceExposicion).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsAssignableTo(typeof(IComandoQueReduceExposicion)));

        marcados.Should().BeEquivalentTo([typeof(RevocarClaveApiDeMiOrganizacionCommand), typeof(CaeManager.Application.Plataforma.Commands.RevocarOperadorCaeExternoPorSoporte.RevocarOperadorCaeExternoPorSoporteCommand)]);
    }

    // La portabilidad salta el gate comercial (no la suspensión operativa): lista cerrada, como la de arriba.
    [Fact]
    public void Solo_la_exportacion_completa_es_portabilidad_del_Tenant_propietario()
    {
        var marcados = typeof(IComandoDePortabilidadDelTenantPropietario).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsAssignableTo(typeof(IComandoDePortabilidadDelTenantPropietario)));

        marcados.Should().BeEquivalentTo([
            typeof(CaeManager.Application.Exportacion.SolicitarExportacionCompletaCommand),
            typeof(CaeManager.Application.Exportacion.DescargarExportacionCompletaCommand)]);
    }

    // Los derechos del interesado saltan el gate comercial durante el impago (D-3, opción (b) del 2026-11-17): lista
    // cerrada de los seis comandos de la solicitud, para que un comando nuevo con el marcador se decida aquí.
    [Fact]
    public void Solo_los_comandos_de_la_solicitud_de_derechos_llevan_la_exencion_de_derechos_del_interesado()
    {
        var marcados = typeof(IComandoDeDerechosDelInteresado).Assembly.GetTypes()
            .Where(t => t.IsClass && t.IsAssignableTo(typeof(IComandoDeDerechosDelInteresado)));

        marcados.Should().BeEquivalentTo([
            typeof(CaeManager.Application.DerechosInteresado.Commands.RegistrarSolicitudDerechoInteresado.RegistrarSolicitudDerechoInteresadoCommand),
            typeof(CaeManager.Application.DerechosInteresado.Commands.TramitarSolicitudDerechoInteresado.VerificarIdentidadSolicitudDerechoCommand),
            typeof(CaeManager.Application.DerechosInteresado.Commands.TramitarSolicitudDerechoInteresado.IniciarTramitacionSolicitudDerechoCommand),
            typeof(CaeManager.Application.DerechosInteresado.Commands.TramitarSolicitudDerechoInteresado.ProrrogarSolicitudDerechoCommand),
            typeof(CaeManager.Application.DerechosInteresado.Commands.TramitarSolicitudDerechoInteresado.ResolverSolicitudDerechoCommand),
            typeof(CaeManager.Application.DerechosInteresado.Commands.TramitarSolicitudDerechoInteresado.DenegarSolicitudDerechoCommand)]);
    }
}

using CaeManager.Application.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// La regla de lectura de la «Nota interna» de las fichas 360, una sola para todas: equipo del Tenant propietario, o
/// Sesión Privilegiada vigente con acceso total al Tenant actual. Cliente, sin rol y roles desconocidos quedan fuera.
/// </summary>
public class PoliticaNotaInternaTests
{
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    [InlineData("Consulta")]
    public async Task El_equipo_del_Tenant_lee_la_nota(string rol)
    {
        (await PoliticaNotaInternaPruebas.Con(rol).PuedeLeerAsync()).Should().BeTrue();
    }

    [Theory]
    [InlineData("Cliente")]
    [InlineData("RolQueNoExiste")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Cliente_sin_rol_o_rol_desconocido_no_leen_la_nota(string? rol)
    {
        (await PoliticaNotaInternaPruebas.Con(rol).PuedeLeerAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Soporte TALVEG: sin rol de negocio, con la sesión vigente y acceso total al Tenant, lee la nota como el equipo.
    /// Las tres capacidades son las mismas que da acceso total en <c>AlcanceDatosService</c>.
    /// </summary>
    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    [InlineData(CapacidadPrivilegio.Aprovisionamiento)]
    public async Task Sesion_privilegiada_con_acceso_total_al_Tenant_lee_la_nota_sin_rol(CapacidadPrivilegio capacidad)
    {
        var politica = PoliticaNotaInternaPruebas.Con(null, PoliticaNotaInternaPruebas.Sesion(capacidad));

        (await politica.PuedeLeerAsync()).Should().BeTrue();
        (await politica.TieneAccesoTotalAlTenantAsync()).Should().BeTrue();
    }

    /// <summary>Capacidades sin acceso total: no abren la nota ni la ficha. Impersonación lee como la persona simulada.</summary>
    [Theory]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Capacidades_sin_acceso_total_no_leen_la_nota_sin_rol(CapacidadPrivilegio capacidad)
    {
        var politica = PoliticaNotaInternaPruebas.Con(null, PoliticaNotaInternaPruebas.Sesion(capacidad));

        (await politica.PuedeLeerAsync()).Should().BeFalse();
        (await politica.TieneAccesoTotalAlTenantAsync()).Should().BeFalse();
    }

    /// <summary>La sesión vale solo dentro del Tenant que abrió: visitar otro Tenant no hereda el acceso.</summary>
    [Fact]
    public async Task Sesion_de_otro_Tenant_no_da_acceso_al_Tenant_actual()
    {
        var otroTenant = Guid.Parse("1b2c3d4e-0000-4000-8000-000000000042");
        var politica = PoliticaNotaInternaPruebas.Con(
            null, PoliticaNotaInternaPruebas.Sesion(CapacidadPrivilegio.SoporteLectura, tenant: otroTenant));

        (await politica.PuedeLeerAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Sin sesión vigente (revocada o caducada, ObtenerAsync da null) el acceso no existe: fallo cerrado.
    /// </summary>
    [Fact]
    public async Task Sin_sesion_vigente_no_hay_acceso_sin_rol()
    {
        (await PoliticaNotaInternaPruebas.Con(null, sesion: null).PuedeLeerAsync()).Should().BeFalse();
    }

    /// <summary>
    /// Invariante de la lista blanca: quien puede guardar la nota (Administrador, DireccionCae, CoordinadorCae, GestorCae)
    /// debe poder leerla, o la sobrescribe a ciegas. Y la lista es exactamente la del equipo, sin Cliente.
    /// </summary>
    [Fact]
    public void Quien_puede_guardar_la_nota_puede_leerla()
    {
        string[] quienGuarda = ["Administrador", "DireccionCae", "CoordinadorCae", "GestorCae"];

        PoliticaNotaInterna.RolesQueVenLaNotaInterna.Should().Contain(quienGuarda);
        PoliticaNotaInterna.RolesQueVenLaNotaInterna.Should().NotContain("Cliente");
    }
}

using CaeManager.Application.Trabajadores.Queries.ObtenerEmpleadoresDeTrabajadoresVisibles;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Subcontratas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Trabajadores;

/// <summary>
/// Opciones de la pastilla «Empresa» de /trabajadores contra Postgres: los empleadores de los Trabajadores que
/// quien mira ve, separados en Empresas y Subcontratas, sin empleadores dados de baja y, con alcance restringido
/// (un usuario de portal ve Trabajadores por Asignación), solo los de esos Trabajadores.
/// </summary>
public class EmpleadoresDeTrabajadoresVisiblesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajadorNorteId;
    private Guid _trabajadorSubcontrataId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var norte = new Empresa("Montajes Norte S.L.", "B10380186");
        var sur = new Empresa("Limpiezas Sur S.L.", "B10380194");
        var deBaja = new Empresa("Obras Este S.L.", "B10380210");
        var sinTrabajadores = new Empresa("Pinturas Oeste S.L.", "B10380228");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Gestionada S.L.", "B10380236", NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.AddRange(norte, sur, deBaja, sinTrabajadores, subcontrata);
        await contexto.SaveChangesAsync();

        var trabajadorNorte = Trabajador.DeEmpresa(norte.Id, "Carla", "Molina Ríos", "X1005105M");
        var trabajadorSubcontrata = Trabajador.DeSubcontrata(subcontrata.Id, "Paula", "Campos Lara", "60005002A");
        contexto.Trabajadores.AddRange(
            trabajadorNorte,
            trabajadorSubcontrata,
            Trabajador.DeEmpresa(sur.Id, "Óscar", "Ferrer Pons", "60005104J"),
            Trabajador.DeEmpresa(deBaja.Id, "Héctor", "Bravo Nieto", "60005102B"));
        await contexto.SaveChangesAsync();
        (_trabajadorNorteId, _trabajadorSubcontrataId) = (trabajadorNorte.Id, trabajadorSubcontrata.Id);

        deBaja.MarcarComoEliminado(Guid.NewGuid());
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Sin_restriccion_son_los_empleadores_de_los_trabajadores_separados_y_sin_los_dados_de_baja()
    {
        var empleadores = await ObtenerAsync(new AlcanceDatosServiceFalso());

        empleadores.Empresas.Select(e => e.RazonSocial).Should().Equal("Limpiezas Sur S.L.", "Montajes Norte S.L.");
        empleadores.Subcontratas.Select(e => e.RazonSocial).Should().Equal("Subcontrata Gestionada S.L.");
    }

    /// <summary>
    /// Alcance restringido a dos Trabajadores (como un usuario de portal que los ve por Asignación): solo sus
    /// empleadores, ni Limpiezas Sur (su Trabajador no es visible) ni ninguna Empresa sin Trabajadores visibles.
    /// </summary>
    [Fact]
    public async Task Con_alcance_restringido_son_solo_los_empleadores_de_los_trabajadores_visibles()
    {
        var portal = new AlcanceDatosServiceFalso(trabajadorIds: [_trabajadorNorteId, _trabajadorSubcontrataId], empresaIdsParaGestion: []);

        var empleadores = await ObtenerAsync(portal);

        empleadores.Empresas.Select(e => e.RazonSocial).Should().Equal("Montajes Norte S.L.");
        empleadores.Subcontratas.Select(e => e.RazonSocial).Should().Equal("Subcontrata Gestionada S.L.");
    }

    [Fact]
    public async Task Sin_trabajadores_visibles_no_hay_opciones()
    {
        var empleadores = await ObtenerAsync(new AlcanceDatosServiceFalso(trabajadorIds: []));

        empleadores.Empresas.Should().BeEmpty();
        empleadores.Subcontratas.Should().BeEmpty();
    }

    private async Task<EmpleadoresDeTrabajadoresDto> ObtenerAsync(AlcanceDatosServiceFalso alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerEmpleadoresDeTrabajadoresVisiblesQueryHandler(contexto, contexto, alcance);
        return await handler.Handle(new ObtenerEmpleadoresDeTrabajadoresVisiblesQuery(), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

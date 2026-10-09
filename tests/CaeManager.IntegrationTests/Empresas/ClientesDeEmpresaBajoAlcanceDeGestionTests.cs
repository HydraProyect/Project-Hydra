using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Empresas;

/// <summary>
/// Fila desplegada de /empresas y pestaña «Clientes empresariales» de Empresa 360:
/// <see cref="ObtenerClientesDeEmpresaQuery"/> contra Postgres. Se comprueba que solo devuelve los Clientes
/// empresariales de Relaciones Empresariales vigentes (ni cerradas, ni con el Cliente dado de baja, ni con una
/// Empresa propia como contraparte), y que el alcance es el de GESTIÓN (REC-153): fuera de él no revela
/// ninguno (#810). Hasta 2026-10-08 esta siembra probaba el resumen de la columna «Presta servicio a»,
/// retirada; las propiedades del predicado y del alcance se conservan aquí sobre la consulta que queda.
/// </summary>
public class ClientesDeEmpresaBajoAlcanceDeGestionTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _norteId;
    private Guid _surId;
    private Guid _esteId;
    private Guid _subcontrataId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        if (await contexto.ParametrosSistema.SingleOrDefaultAsync() is null)
            contexto.ParametrosSistema.Add(new ParametroSistema(30, 15));

        var pegaso = Empresa.CrearComoCliente("Pegaso Cliente S.L.", "B10000024", false, null, null);
        var orion = Empresa.CrearComoCliente("Orion Cliente S.L.", "B10000016", false, null, null);
        var norte = new Empresa("Montajes Norte S.L.", "B10380186");
        var sur = new Empresa("Limpiezas Sur S.L.", "B10380194");
        var este = new Empresa("Obras Este S.L.", "B10380210");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Gestionada S.L.", "B10380236", NivelServicioSubcontrata.Gestionada.ToString());
        contexto.Empresas.AddRange(pegaso, orion, norte, sur, este, subcontrata);
        await contexto.SaveChangesAsync();
        (_norteId, _surId, _esteId, _subcontrataId) = (norte.Id, sur.Id, este.Id, subcontrata.Id);

        var ahora = DateTime.UtcNow;
        var cerrada = RelacionEmpresarial.Crear(sur.Id, orion.Id, ahora.AddDays(-30));
        cerrada.Cerrar(ahora.AddDays(-1));
        contexto.RelacionesEmpresariales.AddRange(
            // Norte presta servicio a dos Clientes empresariales; Pegaso se da de alta antes, Orion va primero por razón social.
            RelacionEmpresarial.Crear(norte.Id, pegaso.Id, ahora),
            RelacionEmpresarial.Crear(norte.Id, orion.Id, ahora),
            // Sur solo tuvo una relación, ya cerrada: no presta servicio hoy.
            cerrada,
            // Shape Subcontrata→Empresa propia: la contraparte NO es un Cliente empresarial.
            RelacionEmpresarial.Crear(subcontrata.Id, norte.Id, ahora));
        await contexto.SaveChangesAsync();

        // Un tercer Cliente empresarial de Norte, dado de baja (con la relación aún abierta): no cuenta.
        var lyra = Empresa.CrearComoCliente("Aries Cliente S.L.", "B10000032", false, null, null);
        contexto.Empresas.Add(lyra);
        await contexto.SaveChangesAsync();
        contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(norte.Id, lyra.Id, ahora));
        await contexto.SaveChangesAsync();
        lyra.MarcarComoEliminado(Guid.NewGuid());
        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Devuelve_solo_los_Clientes_empresariales_de_relaciones_vigentes_por_razon_social()
    {
        var alcance = new AlcanceDatosServiceFalso();

        // «Aries», dado de baja, iría primero por razón social: si contara, la lista empezaría por él.
        (await ClientesDeEmpresaAsync(alcance, _norteId)).Select(c => c.RazonSocial)
            .Should().Equal("Orion Cliente S.L.", "Pegaso Cliente S.L.");
        (await ClientesDeEmpresaAsync(alcance, _surId)).Should().BeEmpty("Sur solo tuvo una relación, ya cerrada");
        (await ClientesDeEmpresaAsync(alcance, _esteId)).Should().BeEmpty("Este no tiene ninguna relación");
        (await ClientesDeEmpresaAsync(alcance, _subcontrataId)).Should().BeEmpty(
            "la Subcontrata presta servicio a una Empresa propia, no a un Cliente empresarial");
    }

    /// <summary>Un usuario de portal (rol Cliente): alcance de gestión vacío, aunque vea las Empresas. No ve nada.</summary>
    [Fact]
    public async Task Un_usuario_de_portal_no_ve_la_cartera_comercial_de_ninguna_Empresa()
    {
        var portal = new AlcanceDatosServiceFalso(empresaIds: [_norteId, _surId], empresaIdsParaGestion: []);

        (await ClientesDeEmpresaAsync(portal, _norteId)).Should().BeEmpty();
        (await ClientesDeEmpresaAsync(portal, _surId)).Should().BeEmpty();
    }

    /// <summary>
    /// No revelación (#810): con alcance de gestión parcial, una Empresa fuera de él devuelve lo mismo que una
    /// Empresa sin Relaciones Empresariales vigentes — vacío — aunque tenga Clientes empresariales.
    /// </summary>
    [Fact]
    public async Task Una_Empresa_fuera_del_alcance_de_gestion_no_revela_sus_Clientes_empresariales()
    {
        var parcial = new AlcanceDatosServiceFalso(empresaIds: [_norteId, _surId], empresaIdsParaGestion: [_surId]);

        (await ClientesDeEmpresaAsync(parcial, _norteId)).Should().BeEmpty(
            "Norte tiene dos Clientes empresariales pero está fuera del alcance de gestión");
        (await ClientesDeEmpresaAsync(parcial, _surId)).Should().BeEmpty("Sur está dentro, pero no tiene ninguno vigente");
        (await ClientesDeEmpresaAsync(new AlcanceDatosServiceFalso(), _norteId)).Should().HaveCount(2, "control: sin restricción Norte sí los devuelve");
    }

    private async Task<IReadOnlyList<ClienteDeEmpresaDto>> ClientesDeEmpresaAsync(AlcanceDatosServiceFalso alcance, Guid empresa)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerClientesDeEmpresaQueryHandler(contexto, alcance);
        return await handler.Handle(new ObtenerClientesDeEmpresaQuery(empresa), CancellationToken.None);
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

using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerResumenClientesDeEmpresas;
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
/// Columna «Presta servicio a» de /empresas: <see cref="ObtenerResumenClientesDeEmpresasQuery"/> contra
/// Postgres. Comparte predicado y alcance con <see cref="ObtenerClientesDeEmpresaQuery"/> (el desplegable de
/// la fila): aquí se comprueba que los dos cuentan lo mismo, que solo cuentan Relaciones Empresariales
/// vigentes con un Cliente empresarial real, y que el alcance es el de GESTIÓN (REC-153).
/// </summary>
public class ResumenClientesDeEmpresasTests : IAsyncLifetime
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
    public async Task Cuenta_las_relaciones_vigentes_con_Clientes_empresariales_y_da_el_primero_por_razon_social()
    {
        var resumen = await ResumirAsync(new AlcanceDatosServiceFalso(), _norteId, _surId, _esteId, _subcontrataId);

        // «Aries», dado de baja, iría primero por razón social: si contara, el resumen sería (3, «Aries…»).
        resumen.Should().ContainKey(_norteId).WhoseValue.Should().Be(new ResumenClientesDeEmpresaDto(2, "Orion Cliente S.L."));
        resumen.Keys.Should().Equal([_norteId],
            "Sur solo tiene una relación cerrada, Este ninguna, y la Subcontrata presta servicio a una Empresa propia, no a un Cliente empresarial");
    }

    /// <summary>Paridad con el desplegable de la fila: mismo total y mismo primero, Empresa a Empresa.</summary>
    [Fact]
    public async Task Cuenta_lo_mismo_que_el_desplegable_de_cada_fila()
    {
        var alcance = new AlcanceDatosServiceFalso();
        Guid[] empresas = [_norteId, _surId, _esteId, _subcontrataId];

        var resumen = await ResumirAsync(alcance, empresas);

        foreach (var empresa in empresas)
        {
            var desplegable = await ClientesDeEmpresaAsync(alcance, empresa);
            if (desplegable.Count == 0)
                resumen.Should().NotContainKey(empresa);
            else
                resumen[empresa].Should().Be(new ResumenClientesDeEmpresaDto(desplegable.Count, desplegable[0].RazonSocial));
        }
    }

    /// <summary>Un usuario de portal (rol Cliente): alcance de gestión vacío, aunque vea las Empresas. No ve nada.</summary>
    [Fact]
    public async Task Un_usuario_de_portal_no_ve_la_cartera_comercial_de_ninguna_Empresa()
    {
        var portal = new AlcanceDatosServiceFalso(empresaIds: [_norteId, _surId], empresaIdsParaGestion: []);

        var resumen = await ResumirAsync(portal, _norteId, _surId);

        resumen.Should().BeEmpty();
    }

    /// <summary>Con alcance de gestión parcial, una Empresa pedida fuera de él no aparece aunque tenga Clientes empresariales.</summary>
    [Fact]
    public async Task Una_Empresa_fuera_del_alcance_de_gestion_no_aparece_aunque_se_pida()
    {
        var parcial = new AlcanceDatosServiceFalso(empresaIds: [_norteId, _surId], empresaIdsParaGestion: [_surId]);

        var resumen = await ResumirAsync(parcial, _norteId, _surId);

        resumen.Should().BeEmpty("Norte tiene dos Clientes empresariales pero está fuera del alcance de gestión, y Sur no tiene ninguno vigente");
        (await ResumirAsync(new AlcanceDatosServiceFalso(), _norteId)).Should().ContainKey(_norteId, "control: sin restricción Norte sí aparece");
    }

    private async Task<IReadOnlyDictionary<Guid, ResumenClientesDeEmpresaDto>> ResumirAsync(AlcanceDatosServiceFalso alcance, params Guid[] empresas)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerResumenClientesDeEmpresasQueryHandler(contexto, alcance);
        return await handler.Handle(new ObtenerResumenClientesDeEmpresasQuery(empresas), CancellationToken.None);
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

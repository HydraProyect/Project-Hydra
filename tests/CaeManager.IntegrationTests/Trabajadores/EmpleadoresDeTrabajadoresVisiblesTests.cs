using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Asignaciones;
using CaeManager.Domain.Centros;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
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
/// (un Usuario de Cliente empresarial ve Trabajadores por Asignación), solo los de esos Trabajadores.
/// </summary>
public class EmpleadoresDeTrabajadoresVisiblesTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    private Guid _trabajadorNorteId;
    private Guid _trabajadorSubcontrataId;
    private Guid _empresaNorteId;
    private Guid _empresaSurId;
    private Guid _trabajadorSurId;

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
        var trabajadorSur = Trabajador.DeEmpresa(sur.Id, "Óscar", "Ferrer Pons", "60005104J");
        contexto.Trabajadores.AddRange(
            trabajadorNorte,
            trabajadorSubcontrata,
            trabajadorSur,
            Trabajador.DeEmpresa(deBaja.Id, "Héctor", "Bravo Nieto", "60005102B"));
        await contexto.SaveChangesAsync();
        (_trabajadorNorteId, _trabajadorSubcontrataId) = (trabajadorNorte.Id, trabajadorSubcontrata.Id);

        (_empresaNorteId, _empresaSurId, _trabajadorSurId) = (norte.Id, sur.Id, trabajadorSur.Id);

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
    /// Visibilidad restringida a dos Trabajadores (como un Usuario de Cliente empresarial que los ve por Asignación): solo sus
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

    /// <summary>Composición con visibilidad real: el Usuario de Cliente empresarial ve ambos tipos de empleador por asignación.</summary>
    [Fact]
    public async Task Usuario_de_Cliente_empresarial_recibe_solo_empleadores_de_Trabajadores_asignados_a_sus_Centros()
    {
        var usuarioId = Guid.NewGuid();
        await using (var contexto = CrearContexto())
        {
            var clienteVisible = Empresa.CrearComoCliente("Cliente visible", "B10380244", false, null, null);
            var clienteAjeno = Empresa.CrearComoCliente("Cliente ajeno", "B10380251", false, null, null);
            var empleadorAjeno = Empresa.CrearComoSubcontrata("Empresa empleadora ajena", null, NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(clienteVisible, clienteAjeno, empleadorAjeno);
            var centroVisible = new Centro(clienteVisible.Id, _empresaNorteId, "Centro visible");
            var centroAjeno = new Centro(clienteAjeno.Id, _empresaSurId, "Centro ajeno");
            contexto.Centros.AddRange(centroVisible, centroAjeno);
            var trabajadorAjeno = Trabajador.DeSubcontrata(empleadorAjeno.Id, "Aitana", "Soler", "44556677L");
            contexto.Trabajadores.Add(trabajadorAjeno);
            contexto.Users.Add(new ApplicationUser
            {
                Id = usuarioId,
                UserName = $"portal-{usuarioId:N}@ejemplo.test",
                Email = $"portal-{usuarioId:N}@ejemplo.test",
                ClienteId = clienteVisible.Id,
                TenantId = _tenant
            });
            await contexto.SaveChangesAsync();
            contexto.Asignaciones.AddRange(
                new Asignacion(_trabajadorNorteId, centroVisible.Id, new DateOnly(2026, 1, 1)),
                new Asignacion(_trabajadorSubcontrataId, centroVisible.Id, new DateOnly(2026, 1, 1)),
                new Asignacion(_trabajadorSurId, centroAjeno.Id, new DateOnly(2026, 1, 1)),
                new Asignacion(trabajadorAjeno.Id, centroAjeno.Id, new DateOnly(2026, 1, 1)));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var alcance = new AlcanceDatosService(
            lectura, new CurrentUserServiceFalso(usuarioId, Roles.Cliente, tenantOrigenId: _tenant),
            new TenantActualAmbiental { TenantId = _tenant }, new SesionPrivilegiadaAusente());
        var visibles = await alcance.ObtenerTrabajadorIdsVisiblesAsync();
        visibles.Should().BeEquivalentTo([_trabajadorNorteId, _trabajadorSubcontrataId]);
        (await alcance.ObtenerEmpresaIdsParaGestionAsync()).Should().BeEmpty(
            "el Usuario de Cliente empresarial puede leer sus Trabajadores y no gestionar las Empresas empleadoras");
        var handler = new ObtenerEmpleadoresDeTrabajadoresVisiblesQueryHandler(lectura, lectura, alcance);
        var empleadores = await handler.Handle(new ObtenerEmpleadoresDeTrabajadoresVisiblesQuery(), CancellationToken.None);
        empleadores.Empresas.Select(e => e.Id).Should().Equal(_empresaNorteId);
        empleadores.Subcontratas.Select(e => e.RazonSocial).Should().Equal("Subcontrata Gestionada S.L.");
    }

    /// <summary>Los filtros EF del Tenant propietario descartan Ids ajenos incluso si el servicio falso los proporciona.</summary>
    [Fact]
    public async Task Empleadores_del_otro_Tenant_propietario_no_aparecen_aunque_sus_Trabajadores_figuren_en_los_Ids_visibles()
    {
        var otroTenant = Guid.NewGuid();
        Guid trabajadorEmpresaAjenoId;
        Guid trabajadorSubcontrataAjenoId;
        Guid empresaAjenaId;
        Guid subcontrataAjenaId;
        await using (var contexto = CrearContexto(otroTenant))
        {
            var empresa = new Empresa("Montajes Norte S.L.", "B10380186");
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Gestionada S.L.", "B10380236", NivelServicioSubcontrata.Gestionada.ToString());
            var deEmpresa = Trabajador.DeEmpresa(empresa.Id, "Carla", "Molina Ríos", "X1005105M");
            var deSubcontrata = Trabajador.DeSubcontrata(subcontrata.Id, "Paula", "Campos Lara", "60005002A");
            contexto.Empresas.AddRange(empresa, subcontrata);
            contexto.Trabajadores.AddRange(deEmpresa, deSubcontrata);
            await contexto.SaveChangesAsync();
            (trabajadorEmpresaAjenoId, trabajadorSubcontrataAjenoId, empresaAjenaId, subcontrataAjenaId) =
                (deEmpresa.Id, deSubcontrata.Id, empresa.Id, subcontrata.Id);
        }

        var empleadores = await ObtenerAsync(new AlcanceDatosServiceFalso(trabajadorIds:
            [_trabajadorNorteId, _trabajadorSubcontrataId, trabajadorEmpresaAjenoId, trabajadorSubcontrataAjenoId]));
        empleadores.Empresas.Select(e => e.Id).Should().Equal(_empresaNorteId);
        empleadores.Empresas.Select(e => e.Id).Should().NotContain(empresaAjenaId);
        empleadores.Subcontratas.Should().ContainSingle();
        empleadores.Subcontratas.Select(e => e.Id).Should().NotContain(subcontrataAjenaId);
        empleadores.Subcontratas.Select(e => e.RazonSocial).Should().Equal("Subcontrata Gestionada S.L.");
    }

    /// <summary>Una baja lógica no cuenta como Trabajador visible; otro Trabajador activo conserva a su empleador.</summary>
    [Fact]
    public async Task Baja_del_unico_Trabajador_retira_el_empleador_y_otra_plantilla_activa_lo_conserva()
    {
        Guid trabajadorSurActivoId;
        await using (var contexto = CrearContexto())
        {
            var trabajadorNorte = await contexto.Trabajadores.SingleAsync(t => t.Id == _trabajadorNorteId);
            var trabajadorSur = await contexto.Trabajadores.SingleAsync(t => t.Id == _trabajadorSurId);
            trabajadorNorte.MarcarComoEliminado(Guid.NewGuid());
            trabajadorSur.MarcarComoEliminado(Guid.NewGuid());
            var activo = Trabajador.DeEmpresa(_empresaSurId, "Nora", "Vidal", "12345678Z");
            contexto.Trabajadores.Add(activo);
            await contexto.SaveChangesAsync();
            trabajadorSurActivoId = activo.Id;
        }

        var empleadores = await ObtenerAsync(new AlcanceDatosServiceFalso(trabajadorIds:
            [_trabajadorNorteId, _trabajadorSurId, trabajadorSurActivoId]));
        empleadores.Empresas.Select(e => e.Id).Should().Equal(_empresaSurId);
        empleadores.Empresas.Select(e => e.Id).Should().NotContain(_empresaNorteId);
        empleadores.Subcontratas.Should().BeEmpty();
    }
    private async Task<EmpleadoresDeTrabajadoresDto> ObtenerAsync(IAlcanceDatosService alcance)
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerEmpleadoresDeTrabajadoresVisiblesQueryHandler(contexto, contexto, alcance);
        return await handler.Handle(new ObtenerEmpleadoresDeTrabajadoresVisiblesQuery(), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto(Guid? tenant = null)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant ?? _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

using CaeManager.Application.Common;
using CaeManager.Application.Gestiones.Commands.RestaurarGestion;
using CaeManager.Application.Proyectos.Commands.RestaurarProyecto;
using CaeManager.Application.Subcontratas.Commands.RestaurarSubcontrata;
using CaeManager.Application.Vehiculos.Commands.RestaurarVehiculo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Gestiones;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Vehiculos;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Centro = CaeManager.Domain.Centros.Centro;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Listados 5/7 (decisión D6, 2026-10-08): los cuatro <c>Restaurar{Tipo}Command</c> nuevos
/// —Subcontrata, Vehículo, Proyecto y Gestión— ejecutados con el cableado de producción: la conexión
/// autentica como <c>cae_app_runtime</c>, RLS filtra por <c>app.tenant_id</c> y el interceptor de
/// auditoría escribe.
///
/// <para>
/// <c>RestaurarEntidadesTests</c> prueba el efecto y la autorización de los comandos, pero se conecta
/// como propietario, que no está sujeto a RLS: ahí la frontera entre Tenants la sostiene solo el
/// <c>TenantId</c> que el comando compara a mano. Aquí se mide la otra mitad: que desde otro Tenant
/// la fila eliminada ni siquiera se ve con <c>IgnoreQueryFilters()</c>, y que la restauración queda
/// en Auditoría.
/// </para>
/// </summary>
public class RestaurarListadosBajoRuntimeTests
{
    private static readonly Guid TenantPropietario = TenantSeedData.IdPorDefecto;

    [Fact]
    public async Task Bajo_RLS_del_Tenant_propietario_restaura_los_cuatro_y_cada_restauracion_queda_en_Auditoria()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var s = await SembrarEliminadosAsync(arnes.CadenaPropietario);

        var r = await RestaurarLosCuatroBajoRuntimeAsync(arnes, TenantPropietario, s);

        new[] { r.Subcontrata, r.Vehiculo, r.Proyecto, r.Gestion }.Should().OnlyContain(x => x.EsExitoso);
        (await EliminadosSegunElPropietarioAsync(arnes.CadenaPropietario, s)).Should().BeEmpty("los cuatro quedan restaurados");

        // La restauración se audita como una modificación que deja EstaEliminado en false: es lo que
        // lee la pantalla de Auditoría, y lo mismo que ya dejan Cliente, Empresa, Centro y Trabajador.
        (await RestauracionesAuditadasAsync(arnes.CadenaPropietario, s)).Should().BeEquivalentTo(
            [("Empresa", s.SubcontrataId), ("Vehiculo", s.VehiculoId), ("Proyecto", s.ProyectoId), ("Gestion", s.GestionId)]);
    }

    [Fact]
    public async Task Bajo_RLS_de_otro_Tenant_las_filas_eliminadas_no_se_ven_ni_se_restauran()
    {
        await using var arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false);
        var s = await SembrarEliminadosAsync(arnes.CadenaPropietario);
        var otroTenant = Guid.NewGuid();

        // Control positivo del instrumento: con el Tenant propietario la misma consulta sí ve la fila.
        // Sin él, «no se ve desde otro Tenant» pasaría también si el rol de runtime no viera nada.
        (await VeElVehiculoSaltandoElFiltroDeEfAsync(arnes, TenantPropietario, s.VehiculoId)).Should().BeTrue();
        (await VeElVehiculoSaltandoElFiltroDeEfAsync(arnes, otroTenant, s.VehiculoId)).Should().BeFalse(
            "IgnoreQueryFilters() quita el filtro de EF, no la política RLS de PostgreSQL");

        var r = await RestaurarLosCuatroBajoRuntimeAsync(arnes, otroTenant, s);

        new[] { r.Subcontrata, r.Vehiculo, r.Proyecto, r.Gestion }.Should().OnlyContain(x => x.EsFallido);
        (await EliminadosSegunElPropietarioAsync(arnes.CadenaPropietario, s)).Should().BeEquivalentTo(
            [s.SubcontrataId, s.VehiculoId, s.ProyectoId, s.GestionId], "desde otro Tenant no se restaura nada");
        (await RestauracionesAuditadasAsync(arnes.CadenaPropietario, s)).Should().BeEmpty();
    }

    private sealed record Sembrado(Guid SubcontrataId, Guid VehiculoId, Guid ProyectoId, Guid GestionId);

    /// <summary>Siembra como propietario (las migraciones y la siembra no son tráfico), en el Tenant propietario.</summary>
    private static async Task<Sembrado> SembrarEliminadosAsync(string cadenaPropietario)
    {
        await using var contexto = ContextoPropietario(cadenaPropietario);
        var quien = Guid.NewGuid();

        var cliente = Empresa.CrearComoCliente("Cliente Runtime S.A.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Runtime S.L.", "B87654323");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Runtime S.L.", "B10380210", "Gestionada");
        contexto.Empresas.AddRange(cliente, empresa, subcontrata);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Runtime");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Restaurar", "Runtime", "77189989B");
        var tipo = new TipoDocumento("Tipo Runtime", null, false, 1, AmbitoAplicacion.Trabajador);
        var vehiculo = Vehiculo.DeEmpresa(empresa.Id, "Furgón", "Transit", "1234ABC");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.Add(trabajador);
        contexto.TiposDocumento.Add(tipo);
        contexto.Vehiculos.Add(vehiculo);
        await contexto.SaveChangesAsync();

        var proyecto = Proyecto.Crear(cliente.Id, centro.Id, "Proyecto Runtime", new DateOnly(2026, 10, 1), null, null);
        var gestion = new Gestion(trabajador.Id, centro.Id, tipo.Id);
        contexto.Proyectos.Add(proyecto);
        contexto.Gestiones.Add(gestion);
        await contexto.SaveChangesAsync();

        subcontrata.MarcarComoEliminado(quien);
        vehiculo.MarcarComoEliminado(quien);
        proyecto.MarcarComoEliminado(quien);
        gestion.MarcarComoEliminado(quien);
        await contexto.SaveChangesAsync();

        return new Sembrado(subcontrata.Id, vehiculo.Id, proyecto.Id, gestion.Id);
    }

    private static async Task<(Result Subcontrata, Result Vehiculo, Result Proyecto, Result Gestion)> RestaurarLosCuatroBajoRuntimeAsync(
        ArnesDeArranqueRuntime arnes, Guid tenantId, Sembrado s)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var tenant = ambito.ServiceProvider.GetRequiredService<ITenantActual>();
        var alcance = new AlcanceDatosServiceFalso();

        using var ambitoTenant = AmbitoTenantExplicito.Establecer(tenantId);

        return (
            await new RestaurarSubcontrataCommandHandler(contexto, tenant, alcance, contexto).Handle(new RestaurarSubcontrataCommand(s.SubcontrataId), CancellationToken.None),
            await new RestaurarVehiculoCommandHandler(contexto, tenant, alcance, contexto).Handle(new RestaurarVehiculoCommand(s.VehiculoId), CancellationToken.None),
            await new RestaurarProyectoCommandHandler(contexto, tenant, alcance, contexto).Handle(new RestaurarProyectoCommand(s.ProyectoId), CancellationToken.None),
            await new RestaurarGestionCommandHandler(contexto, tenant, alcance, contexto).Handle(new RestaurarGestionCommand(s.GestionId), CancellationToken.None));
    }

    private static async Task<bool> VeElVehiculoSaltandoElFiltroDeEfAsync(ArnesDeArranqueRuntime arnes, Guid tenantId, Guid vehiculoId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var contexto = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

        using var ambitoTenant = AmbitoTenantExplicito.Establecer(tenantId);

        return await contexto.Vehiculos.IgnoreQueryFilters().AnyAsync(v => v.Id == vehiculoId);
    }

    /// <summary>
    /// Se lee como propietario y sin el filtro global de EF: la pregunta es «¿qué quedó escrito?», y
    /// leerlo con el mismo filtro que podría esconderlo convertiría una fila ausente en un verde.
    /// </summary>
    private static async Task<List<Guid>> EliminadosSegunElPropietarioAsync(string cadenaPropietario, Sembrado s)
    {
        await using var contexto = ContextoPropietario(cadenaPropietario);

        var eliminados = new List<Guid>();
        if ((await contexto.Empresas.IgnoreQueryFilters().SingleAsync(e => e.Id == s.SubcontrataId)).EstaEliminado) eliminados.Add(s.SubcontrataId);
        if ((await contexto.Vehiculos.IgnoreQueryFilters().SingleAsync(v => v.Id == s.VehiculoId)).EstaEliminado) eliminados.Add(s.VehiculoId);
        if ((await contexto.Proyectos.IgnoreQueryFilters().SingleAsync(p => p.Id == s.ProyectoId)).EstaEliminado) eliminados.Add(s.ProyectoId);
        if ((await contexto.Gestiones.IgnoreQueryFilters().SingleAsync(g => g.Id == s.GestionId)).EstaEliminado) eliminados.Add(s.GestionId);
        return eliminados;
    }

    private static async Task<List<(string Tipo, Guid Id)>> RestauracionesAuditadasAsync(string cadenaPropietario, Sembrado s)
    {
        await using var contexto = ContextoPropietario(cadenaPropietario);
        Guid[] ids = [s.SubcontrataId, s.VehiculoId, s.ProyectoId, s.GestionId];

        var filas = await contexto.RegistrosAuditoria.IgnoreQueryFilters()
            .Where(r => ids.Contains(r.EntidadId) && r.Accion == "Modificado"
                        && r.DatosDespues != null && r.DatosDespues.Contains("\"EstaEliminado\":false"))
            .Select(r => new { r.EntidadTipo, r.EntidadId })
            .ToListAsync();

        return filas.Select(f => (f.EntidadTipo, f.EntidadId)).ToList();
    }

    private static CaeManagerDbContext ContextoPropietario(string cadenaPropietario)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = TenantPropietario };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

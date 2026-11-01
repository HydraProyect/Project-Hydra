using System.Text.Json;
using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Exportacion;

/// <summary>
/// El rastro del CSV del Cuadro de dirección (C-05), que se genera en el circuito de Blazor y no tiene HttpContext: lo
/// único que lo une al xlsx es <see cref="IRegistroExportacionService"/>. Aquí se mide la fila que queda al escribirla
/// <b>como <c>cae_app_runtime</c></b> con los interceptores de producción (sellado de Tenant y sesión RLS) y sin ningún
/// HttpContext, con un Actor fijo: no ejerce <c>ActorAuditoriaDesdeSesion</c> (lo cubren sus propios tests). La siembra y
/// las migraciones van como propietario.
/// </summary>
public class ExportacionCsvCuadroDireccionBajoRuntimeTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var propietario = CrearContexto(_cadenaConexion, _tenant);
        await propietario.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_rastro_del_CSV_queda_en_la_auditoria_del_Tenant_con_el_Actor_real_y_sin_HttpContext()
    {
        using (AmbitoTenantExplicito.Establecer(_tenant))
        {
            await using var runtime = CrearContexto(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), null, conRls: true);
            await Registro(runtime).RegistrarAsync(
                "CuadroDireccion", 3,
                new Dictionary<string, string> { ["listado"] = "cuadroDireccionCsv", ["tabla"] = "cartera", ["anyo"] = "2026", ["mes"] = "10" });
        }

        await using var lectura = CrearContexto(_cadenaConexion, _tenant);
        var fila = (await lectura.RegistrosAuditoria.Where(r => r.Accion == RegistroAuditoria.AccionExportacion).ToListAsync())
            .Should().ContainSingle().Subject;
        fila.EntidadTipo.Should().Be("CuadroDireccion");
        fila.TenantId.Should().Be(_tenant, "la fila va al Tenant propietario de los datos exportados");
        fila.UsuarioId.Should().Be(_usuario);
        fila.ActorRealUsuarioId.Should().Be(_usuario);
        using var datos = JsonDocument.Parse(fila.DatosDespues!);
        datos.RootElement.GetProperty("filas").GetInt32().Should().Be(3);
        datos.RootElement.GetProperty("criterios").GetProperty("tabla").GetString().Should().Be("cartera");

        await using var ajeno = CrearContexto(BaseDatosPostgresDePruebas.CadenaComoRuntime(_cadenaConexion), null, conRls: true);
        using (AmbitoTenantExplicito.Establecer(_otroTenant))
        {
            (await ajeno.RegistrosAuditoria.Where(r => r.Accion == RegistroAuditoria.AccionExportacion).CountAsync())
                .Should().Be(0, "el rastro de un Tenant no se ve desde otro, bajo RLS");
        }
    }

    private RegistroExportacionService Registro(CaeManagerDbContext contexto) => new(
        new ActorFijo(ActorAuditoria.Normal(_usuario)),
        new RegistroAccesoDatoSensibleRepository(contexto, NullLogger<RegistroAccesoDatoSensibleRepository>.Instance));

    private static CaeManagerDbContext CrearContexto(string cadena, Guid? tenantId, bool conRls = false)
    {
        ITenantActual tenant = tenantId is { } id ? new TenantActualAmbiental { TenantId = id } : new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>().UseNpgsql(cadena, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"));
        if (conRls)
            opciones.AddInterceptors(
                new TenantSelladoInterceptor(tenant),
                new TenantRlsConnectionInterceptor(tenant, new SinClienteActivo(), new CurrentUserServiceFalso(), BaseDatosPostgresDePruebas.FirmanteContextoRls));
        else
            opciones.AddInterceptors(new TenantSelladoInterceptor(tenant));

        return new CaeManagerDbContext(opciones.Options, new EphemeralDataProtectionProvider(), tenant);
    }

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    private sealed class SinClienteActivo : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => null;
        public Guid? AsignacionOperacionIdSeleccionada => null;
        public Guid? SesionPrivilegiadaIdSeleccionada => null;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}

using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Migrations.PostgreSQL.Migrations;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Tenants;

/// <summary>
/// Migración <c>RevocarAsignacionesOperadorDelegadoConRolDePropiedad</c> (P8,
/// decisión del propietario 2026-09-23). Se siembra contra el esquema anterior
/// —sin columnas de revocación— para reproducir las bases reales con filas
/// heredadas que conceden Administrador o Dirección CAE, y se comprueba que la
/// migración las revoca sin borrar nada, deja intactas las de Gestor CAE y
/// Consulta, y no se deja bajar mientras quede alguna revocada.
/// </summary>
public class RevocarAsignacionesOperadorDelegadoConRolDePropiedadMigrationTests : IAsyncLifetime
{
    private const string MigracionAntes = "AgregarCapacidadOperadorCaeExternoATenant";
    private const string MigracionObjetivo = "RevocarAsignacionesOperadorDelegadoConRolDePropiedad";

    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _propietario = Guid.NewGuid();   // Refrielectric: Tenant propietario
    private readonly Guid _operadorCae = Guid.NewGuid();   // ArcosSPA: Operador CAE externo
    private readonly Dictionary<string, Guid> _asignaciones = [];
    private Guid _carteraAdministrador;
    private Guid _carteraDireccion;
    private Guid _carteraGestor;

    public async Task InitializeAsync()
    {
        await using (var contexto = CrearContexto())
        {
            await Migrador(contexto).MigrateAsync(MigracionAntes);
        }

        await using (var contexto = CrearContexto())
        {
            var delegacion = new DelegacionTenant(_operadorCae, _propietario);
            contexto.DelegacionesTenant.Add(delegacion);
            await contexto.SaveChangesAsync();

            // Esquema anterior: la entidad actual ya lleva las columnas de
            // revocación, así que las filas se insertan en SQL crudo.
            foreach (var rol in new[] { "Administrador", "DireccionCae", "GestorCae", "Consulta" })
            {
                var id = Guid.NewGuid();
                _asignaciones[rol] = id;
                await contexto.Database.ExecuteSqlInterpolatedAsync($"""
                    INSERT INTO "AsignacionesOperadorDelegado" ("Id", "DelegacionTenantId", "UsuarioId", "Rol", "CreadoEnUtc")
                    VALUES ({id}, {delegacion.Id}, {Guid.NewGuid()}, {rol}, {DateTime.UtcNow})
                    """);
            }

            var ahora = DateTime.UtcNow;
            var externa = AsignacionOperacion.Externa(
                _propietario, _operadorCae, ServicioCae.Outbound, AmbitoAsignacion.Universal,
                ahora.AddDays(-10), null, ahora.AddDays(-10));
            contexto.AsignacionesOperacion.Add(externa);
            var administrador = AsignacionCartera.Externa(
                externa, Guid.NewGuid(), "Administrador", AmbitoAsignacion.Universal, ahora.AddDays(-5), null, ahora.AddDays(-5));
            var direccion = AsignacionCartera.Externa(
                externa, Guid.NewGuid(), "DireccionCae", AmbitoAsignacion.Universal, ahora.AddDays(-5), ahora.AddDays(30), ahora.AddDays(-5));
            var gestor = AsignacionCartera.Externa(
                externa, Guid.NewGuid(), "GestorCae", AmbitoAsignacion.Universal, ahora.AddDays(-5), null, ahora.AddDays(-5));
            contexto.AsignacionesCartera.AddRange(administrador, direccion, gestor);
            await contexto.SaveChangesAsync();

            _carteraAdministrador = administrador.Id;
            _carteraDireccion = direccion.Id;
            _carteraGestor = gestor.Id;
        }

        await using (var contexto = CrearContexto())
        {
            await Migrador(contexto).MigrateAsync(MigracionObjetivo);
        }
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Revoca_las_asignaciones_de_Operador_Delegado_con_rol_de_Propiedad_sin_borrar_ninguna()
    {
        await using var contexto = CrearContexto();
        var filas = await contexto.AsignacionesOperadorDelegadoConRevocadas.ToListAsync();

        filas.Should().HaveCount(4, "revocar no borra");
        foreach (var rol in new[] { "Administrador", "DireccionCae" })
        {
            var fila = filas.Single(a => a.Id == _asignaciones[rol]);
            fila.EstaRevocada.Should().BeTrue();
            fila.MotivoRevocacion.Should().Be(RevocarAsignacionesOperadorDelegadoConRolDePropiedad.MotivoRevocacion);
        }

        foreach (var rol in new[] { "GestorCae", "Consulta" })
        {
            filas.Single(a => a.Id == _asignaciones[rol]).EstaRevocada.Should().BeFalse($"{rol} es un rol de Operación válido");
        }

        (await contexto.AsignacionesOperadorDelegado.Select(a => a.Rol).ToListAsync())
            .Should().BeEquivalentTo(["GestorCae", "Consulta"], "la vista de lectura solo ve las vigentes");
    }

    [Fact]
    public async Task Cierra_las_carteras_externas_con_rol_de_Propiedad_y_deja_vigente_la_del_Gestor_CAE()
    {
        await using var contexto = CrearContexto();
        var carteras = await contexto.AsignacionesCartera.ToListAsync();

        carteras.Should().HaveCount(3, "cerrar no borra");
        foreach (var id in new[] { _carteraAdministrador, _carteraDireccion })
        {
            var cartera = carteras.Single(c => c.Id == id);
            cartera.Estado.Should().Be(EstadoAsignacion.Cerrada);
            cartera.MotivoCierre.Should().Be(MotivoCierreAsignacion.Revocada);
            cartera.VigenciaHasta.Should().NotBeNull().And.BeBefore(DateTime.UtcNow.AddMinutes(1),
                "la vigencia se recorta a la revocación, también la que tenía fin futuro");
        }

        carteras.Single(c => c.Id == _carteraGestor).Estado.Should().Be(EstadoAsignacion.Vigente);
    }

    /// <summary>
    /// Ninguna ruta antigua puede reactivarlas: bajar la migración borraría la
    /// marca de revocación, así que se niega mientras quede alguna revocada.
    /// </summary>
    [Fact]
    public async Task Bajar_la_migracion_con_filas_revocadas_falla_en_vez_de_reactivarlas()
    {
        await using (var contexto = CrearContexto())
        {
            await Migrador(contexto).Invoking(m => m.MigrateAsync(MigracionAntes))
                .Should().ThrowAsync<Npgsql.PostgresException>();
        }

        await using var verificacion = CrearContexto();
        (await verificacion.AsignacionesOperadorDelegadoConRevocadas.CountAsync(a => a.RevocadaEnUtc != null))
            .Should().Be(2, "el intento fallido no toca la marca de revocación");
    }

    private static IMigrator Migrador(CaeManagerDbContext contexto) =>
        contexto.GetInfrastructure().GetRequiredService<IMigrator>();

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _propietario };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

using CaeManager.Application.Common;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.Web.Tests;

public class WebhookTenantResolverSeguridadTests
{
    [Fact]
    public async Task El_secreto_y_la_suscripcion_correctos_resuelven_el_Tenant_propietario()
    {
        await using var contexto = await CrearContexto();
        var suscripcion = await contexto.SuscripcionesWebhook.IgnoreQueryFilters().SingleAsync();

        var resultado = await new WebhookTenantResolver(contexto).VerificarAsync(
            suscripcion.ConexionIntegracionId, "secreto-de-prueba", "suscripcion-de-prueba", default);

        resultado.Verificado.Should().BeTrue();
        resultado.TenantId.Should().Be(suscripcion.TenantId);
        contexto.ChangeTracker.HasChanges().Should().BeFalse();
    }

    [Theory]
    [InlineData("secreto-incorrecto", "suscripcion-de-prueba", false, false)]
    [InlineData(null, "suscripcion-de-prueba", false, false)]
    [InlineData("", "suscripcion-de-prueba", false, false)]
    [InlineData(" ", "suscripcion-de-prueba", false, false)]
    [InlineData("secreto-de-prueba", null, false, false)]
    [InlineData("secreto-de-prueba", "", false, false)]
    [InlineData("secreto-de-prueba", " ", false, false)]
    [InlineData("secreto-de-prueba", "otra-suscripcion", false, false)]
    [InlineData("secreto-de-prueba", "suscripcion-de-prueba", true, false)]
    [InlineData("secreto-de-prueba", "suscripcion-de-prueba", false, true)]
    public async Task Una_verificacion_fallida_no_revela_el_Tenant_propietario_ni_modifica_datos(
        string? secreto, string? subscriptionId, bool caducada, bool conexionAjena)
    {
        await using var contexto = await CrearContexto(caducada);
        var suscripcion = await contexto.SuscripcionesWebhook.IgnoreQueryFilters().SingleAsync();

        var resultado = await new WebhookTenantResolver(contexto).VerificarAsync(
            conexionAjena ? Guid.NewGuid() : suscripcion.ConexionIntegracionId,
            secreto!, subscriptionId, default);

        resultado.Verificado.Should().BeFalse();
        resultado.TenantId.Should().BeNull();
        contexto.ChangeTracker.HasChanges().Should().BeFalse();
        (await contexto.SuscripcionesWebhook.IgnoreQueryFilters().CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Un_ClientState_vacio_guardado_no_se_acepta_con_un_clientState_vacio_recibido()
    {
        await using var contexto = await CrearContexto();
        var suscripcion = await contexto.SuscripcionesWebhook.IgnoreQueryFilters().SingleAsync();
        contexto.Entry(suscripcion).Property(s => s.ClientState).CurrentValue = "";
        await contexto.SaveChangesAsync();

        var resultado = await new WebhookTenantResolver(contexto).VerificarAsync(
            suscripcion.ConexionIntegracionId, "", "suscripcion-de-prueba", default);

        resultado.Verificado.Should().BeFalse();
        resultado.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task Dos_suscripciones_de_Tenants_distintos_para_la_misma_conexion_rechazan_en_vez_de_elegir()
    {
        await using var contexto = await CrearContexto();
        var suscripcion = await contexto.SuscripcionesWebhook.IgnoreQueryFilters().SingleAsync();
        var duplicada = new SuscripcionWebhook(suscripcion.ConexionIntegracionId, "suscripcion-de-prueba",
            "secreto-de-prueba", DateTime.UtcNow.AddDays(1));
        contexto.SuscripcionesWebhook.Add(duplicada);
        contexto.Entry(duplicada).Property(s => s.TenantId).CurrentValue = Guid.NewGuid();
        await contexto.SaveChangesAsync();

        var resultado = await new WebhookTenantResolver(contexto).VerificarAsync(
            suscripcion.ConexionIntegracionId, "secreto-de-prueba", "suscripcion-de-prueba", default);

        resultado.Verificado.Should().BeFalse();
        resultado.TenantId.Should().BeNull();
    }

    private static async Task<ContextoSuscripciones> CrearContexto(bool caducada = false)
    {
        var conexion = new SqliteConnection("Data Source=:memory:");
        await conexion.OpenAsync();
        conexion.CreateFunction<string, string?>("app_tenant_de_webhook_graph", id =>
        {
            using var comando = conexion.CreateCommand();
            comando.CommandText = "SELECT CASE WHEN count(*) = 1 THEN min(\"TenantId\") ELSE NULL END FROM \"SuscripcionesWebhook\" WHERE \"ConexionIntegracionId\" = $id";
            comando.Parameters.AddWithValue("$id", id);
            var valor = comando.ExecuteScalar();
            return valor is null or DBNull ? null : (string)valor;
        });
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseSqlite(conexion, contextOwnsConnection: true).Options;
        var contexto = new ContextoSuscripciones(options);
        await contexto.Database.EnsureCreatedAsync();
        var suscripcion = new SuscripcionWebhook(Guid.NewGuid(), "suscripcion-de-prueba",
            "secreto-de-prueba", DateTime.UtcNow.AddDays(caducada ? -1 : 1));
        contexto.SuscripcionesWebhook.Add(suscripcion);
        contexto.Entry(suscripcion).Property(s => s.TenantId).CurrentValue = Guid.NewGuid();
        await contexto.SaveChangesAsync();
        return contexto;
    }

    // Solo se adapta el modelo relacional a SQLite; el resolver ejecutado es el real.
    private sealed class ContextoSuscripciones(DbContextOptions<CaeManagerDbContext> options)
        : CaeManagerDbContext(options, new EphemeralDataProtectionProvider(),
            new TenantActualAmbiental { TenantId = Guid.NewGuid() })
    {
        private Guid? TenantAmbito => AmbitoTenantExplicito.TenantIdActual;

        protected override void OnModelCreating(ModelBuilder builder)
        {
            foreach (var tipo in builder.Model.GetEntityTypes().Select(t => t.ClrType).ToArray())
                builder.Ignore(tipo);

            builder.Entity<SuscripcionWebhook>().HasKey(s => s.Id);
            builder.Entity<SuscripcionWebhook>().HasQueryFilter(s => s.TenantId == TenantAmbito);
        }
    }
}

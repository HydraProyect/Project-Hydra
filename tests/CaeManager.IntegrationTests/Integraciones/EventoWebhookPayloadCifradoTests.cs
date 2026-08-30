using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Integraciones;

/// <summary>
/// Auditoría módulo 6: <see cref="EventoWebhook.PayloadCrudo"/> contiene
/// PHI/PII de conversación (cuerpos, teléfonos, nombres de WhatsApp/Graph) —
/// se cifra en reposo con el mismo mecanismo que ya protege RefreshToken/
/// ClientState, con compatibilidad con lo ya escrito antes de este cambio
/// (mismo criterio que <c>DiskFileStorageService.AbrirAsync</c>).
/// </summary>
public class EventoWebhookPayloadCifradoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task El_payload_no_viaja_en_claro_en_la_columna()
    {
        Guid eventoId;
        await using (var contexto = CrearContexto())
        {
            var conexion = new ConexionIntegracion("buzon@ejemplo.com", "Buzón M365");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();

            var evento = new EventoWebhook(conexion.Id, "{\"telefono\":\"+34600111222\",\"nombre\":\"Chris\"}");
            new EventoWebhookRepository(contexto).Agregar(evento);
            await contexto.SaveChangesAsync();
            eventoId = evento.Id;
        }

        await using var contextoLectura = CrearContexto();
        var columnaCruda = await contextoLectura.Database
            .SqlQuery<string>($"""SELECT "PayloadCrudo" AS "Value" FROM "EventosWebhook" WHERE "Id" = {eventoId}""")
            .SingleAsync();

        columnaCruda.Should().NotContain("+34600111222");
        columnaCruda.Should().NotContain("Chris");
    }

    [Fact]
    public async Task Se_descifra_correctamente_al_leer_con_ef()
    {
        Guid eventoId;
        const string payloadOriginal = "{\"telefono\":\"+34600111222\"}";
        await using (var contexto = CrearContexto())
        {
            var conexion = new ConexionIntegracion("buzon@ejemplo.com", "Buzón M365");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();

            var evento = new EventoWebhook(conexion.Id, payloadOriginal);
            new EventoWebhookRepository(contexto).Agregar(evento);
            await contexto.SaveChangesAsync();
            eventoId = evento.Id;
        }

        await using var contextoLectura = CrearContexto();
        var leido = await contextoLectura.EventosWebhook.SingleAsync(e => e.Id == eventoId);

        leido.PayloadCrudo.Should().Be(payloadOriginal);
    }

    /// <summary>Mismo criterio que DiskFileStorageServiceTests: un valor que no descifra con el protector actual es un payload legado guardado en claro antes de que existiera este cifrado — se sirve tal cual, no rompe la lectura.</summary>
    [Fact]
    public async Task Un_payload_legado_guardado_en_claro_antes_del_cifrado_se_sigue_leyendo()
    {
        Guid eventoId;
        const string payloadLegadoEnClaro = "{\"legado\":true}";
        await using (var contexto = CrearContexto())
        {
            var conexion = new ConexionIntegracion("buzon@ejemplo.com", "Buzón M365");
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();

            var evento = new EventoWebhook(conexion.Id, "{\"placeholder\":true}");
            new EventoWebhookRepository(contexto).Agregar(evento);
            await contexto.SaveChangesAsync();
            eventoId = evento.Id;

            // Sobrescribe la columna por SQL directo, saltándose el
            // ValueConverter — así queda en claro, como si la fila viniera
            // de antes de activar el cifrado.
            await contexto.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "EventosWebhook" SET "PayloadCrudo" = {payloadLegadoEnClaro} WHERE "Id" = {eventoId}""");
        }

        await using var contextoLectura = CrearContexto();
        var leido = await contextoLectura.EventosWebhook.SingleAsync(e => e.Id == eventoId);

        leido.PayloadCrudo.Should().Be(payloadLegadoEnClaro);
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

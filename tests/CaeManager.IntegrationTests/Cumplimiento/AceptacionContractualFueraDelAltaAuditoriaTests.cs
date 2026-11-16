using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Cumplimiento;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Cumplimiento;

/// <summary>
/// #2009, D-6: la aceptación contractual fuera del alta (<c>SolicitudAltaId = null</c>) deja en la auditoría al Actor
/// real que aceptó, la escribe el <see cref="AuditoriaInterceptor"/> real, y no copia la IP al historial. La entidad
/// guarda la cuenta (<see cref="AceptacionContractualTenant.UsuarioId"/>); la fila «Creado» es la que dice quién estaba
/// detrás del teclado.
/// </summary>
public class AceptacionContractualFueraDelAltaAuditoriaTests : IAsyncLifetime
{
    private const string Ip = "198.51.100.23";
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _administrador = Guid.NewGuid();
    // Sin clave foránea a Tenants (migración AnadeAceptacionesContractualesTenant): basta con un Tenant del ámbito.
    private readonly TenantActualAmbiental _tenantActual = new() { TenantId = Guid.NewGuid() };

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task La_aceptacion_fuera_del_alta_deja_al_Actor_real_en_la_auditoria_sin_la_IP()
    {
        Guid aceptacionId;
        await using (var contexto = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            var aceptacion = new AceptacionContractualTenant(_administrador, solicitudAltaId: null,
                new AceptacionDeclarada("Encargo", "1.0", new string('d', 64), DeclaraFacultades: true, Ip, ahora), ahora);
            contexto.AceptacionesContractualesTenant.Add(aceptacion);
            await contexto.SaveChangesAsync();
            aceptacionId = aceptacion.Id;
        }

        await using var lectura = CrearContexto();
        var fila = await lectura.RegistrosAuditoria.SingleAsync(r =>
            r.EntidadTipo == nameof(AceptacionContractualTenant) && r.EntidadId == aceptacionId);

        fila.Accion.Should().Be("Creado");
        fila.ActorRealUsuarioId.Should().Be(_administrador, "el Actor real es quien aceptó");
        fila.UsuarioId.Should().Be(_administrador, "sin Usuario simulado, la cuenta efectiva es la del Actor real");
        fila.DatosDespues.Should().NotBeNull().And.NotContain(Ip, "la IP es evidencia, no historial");
        fila.DatosDespues.Should().Contain("Encargo");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorAuditoriaFalso(ActorAuditoria.Normal(_administrador))),
                new TenantSelladoInterceptor(_tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), _tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}

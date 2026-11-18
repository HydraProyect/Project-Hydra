using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Integraciones;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Issue #2124, acción Confirmar: reconocer el aviso del hueco de un buzón de Microsoft 365 deja su fila de auditoría con
/// la acción propia (<see cref="RegistroAuditoria.AccionHuecoReconocido"/>), el Actor real, el Usuario simulado, la fecha y
/// la marca reconocida entre los valores de antes. La escribe el <see cref="AuditoriaInterceptor"/> en el mismo guardado
/// (<see cref="IAccionAuditoriaPropia"/>), la acción cabe en la columna y no sobrevive al guardado.
/// </summary>
public class AuditoriaReconocerAvisoHuecoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _actorReal = Guid.NewGuid();
    private readonly Guid _usuarioSimulado = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Reconocer_el_aviso_deja_su_fila_con_el_actor_la_fecha_y_la_marca_reconocida()
    {
        var marca = new DateTime(2026, 11, 16, 8, 0, 0, DateTimeKind.Utc);
        Guid conexionId;
        await using (var contexto = CrearContexto())
        {
            var conexion = new ConexionIntegracion("cae@operador.test", "Buzón CAE");
            conexion.FijarIdentidadBuzon("inbox-cae");
            conexion.MarcarIdentidadVerificada(DateTime.UtcNow);
            conexion.RegistrarNotificacionDescartada(marca + ConexionIntegracion.RetrasoMaximoNotificacion).Should().BeTrue();
            contexto.ConexionesIntegracion.Add(conexion);
            await contexto.SaveChangesAsync();
            conexionId = conexion.Id;
        }

        var antes = DateTime.UtcNow;
        await using (var contexto = CrearContexto())
        {
            var conexion = await contexto.ConexionesIntegracion.SingleAsync(c => c.Id == conexionId);
            conexion.ReconocerAvisoHueco().Should().BeTrue();
            await contexto.SaveChangesAsync();

            // Misma instancia, otro guardado: ya no es un reconocimiento.
            conexion.FijarAcuseAutomatico(!conexion.AcuseAutomaticoActivo);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        (await lectura.ConexionesIntegracion.SingleAsync(c => c.Id == conexionId)).HuecoPendienteDesdeUtc.Should().BeNull();
        var filas = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(ConexionIntegracion) && r.EntidadId == conexionId && r.Accion != "Creado")
            .OrderBy(r => r.FechaUtc)
            .ToListAsync();

        filas.Select(f => f.Accion).Should().Equal(RegistroAuditoria.AccionHuecoReconocido, "Modificado");
        var reconocimiento = filas[0];
        reconocimiento.ActorRealUsuarioId.Should().Be(_actorReal);
        reconocimiento.UsuarioId.Should().Be(_usuarioSimulado);
        reconocimiento.FechaUtc.Should().BeOnOrAfter(antes.AddSeconds(-1));
        reconocimiento.DatosAntes.Should().Contain(nameof(ConexionIntegracion.HuecoPendienteDesdeUtc)).And.Contain("2026-11-16");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var actor = new ActorAuditoria(_actorReal, _usuarioSimulado, TipoViaAcceso.Normal, null);
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorAuditoriaFalso(actor)),
                new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}

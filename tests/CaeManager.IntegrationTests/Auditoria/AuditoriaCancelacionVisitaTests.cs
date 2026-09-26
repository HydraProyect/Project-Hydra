using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// FS-11 (auditoría UX de flujos sin salida, 2026-09-24): cancelar y reactivar una
/// Visita quedan en la auditoría con el Actor real separado del Usuario simulado,
/// la fecha y el motivo. No hay código de auditoría específico: lo garantiza el
/// <see cref="AuditoriaInterceptor"/> genérico sobre las columnas de estado de la
/// Visita, y este test fija que no se escapan de él.
/// </summary>
public class AuditoriaCancelacionVisitaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Cancelar_y_reactivar_dejan_actor_real_usuario_simulado_fecha_y_motivo()
    {
        var visitaId = await SembrarVisitaAsync();

        var gestor = Guid.NewGuid();
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(gestor))))
        {
            var resultado = await new CancelarVisitaCommandHandler(new VisitaRepository(contexto), contexto, new AlcanceDatosServiceFalso())
                .Handle(new CancelarVisitaCommand(visitaId, "Obra aplazada"), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        }

        // Reactivación bajo impersonación: el actor real no es el usuario simulado,
        // y la auditoría no puede atribuir la acción solo a este último.
        var actorReal = Guid.NewGuid();
        var simulado = Guid.NewGuid();
        var sesion = Guid.NewGuid();
        var antes = DateTime.UtcNow.AddSeconds(-5);
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(
                         new ActorAuditoria(actorReal, simulado, TipoViaAcceso.SesionPrivilegiada, sesion))))
        {
            var resultado = await new ReactivarVisitaCommandHandler(
                    new VisitaRepository(contexto), contexto, new AlcanceDatosServiceFalso(), new EvaluadorExpedienteNulo(),
                    Microsoft.Extensions.Logging.Abstractions.NullLogger<ReactivarVisitaCommandHandler>.Instance)
                .Handle(new ReactivarVisitaCommand(visitaId, "Se retoma la obra"), CancellationToken.None);
            resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        }

        await using var lectura = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));
        var registros = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(Visita) && r.EntidadId == visitaId && r.Accion == "Modificado")
            .OrderBy(r => r.FechaUtc)
            .ToListAsync();

        registros.Should().HaveCount(2, "una fila por la cancelación y otra por la reactivación");
        var (cancelacion, reactivacion) = (registros[0], registros[1]);

        cancelacion.ActorRealUsuarioId.Should().Be(gestor);
        cancelacion.DatosDespues.Should().Contain($"\"{nameof(Visita.EstaCancelada)}\":true")
            .And.Contain("Obra aplazada");

        reactivacion.ActorRealUsuarioId.Should().Be(actorReal, "el Actor real queda registrado aparte");
        reactivacion.UsuarioId.Should().Be(simulado, "y el Usuario simulado también");
        reactivacion.FechaUtc.Should().BeAfter(antes);
        reactivacion.DatosAntes.Should().Contain($"\"{nameof(Visita.EstaCancelada)}\":true")
            .And.Contain("Obra aplazada", "la reactivación deja constancia de la cancelación que deshace");
        reactivacion.DatosDespues.Should().Contain($"\"{nameof(Visita.EstaCancelada)}\":false")
            .And.Contain(nameof(Visita.ReactivadaEnUtc))
            .And.Contain("Se retoma la obra");
    }

    private async Task<Guid> SembrarVisitaAsync()
    {
        await using var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid())));
        var titular = Empresa.CrearComoCliente("Titular auditado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista auditada", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Sur");
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var visita = new Visita(centro.Id, hoy.AddDays(2), hoy.AddDays(3), null);
        contexto.Empresas.AddRange(titular, proveedora);
        contexto.Centros.Add(centro);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();
        return visita.Id;
    }

    private CaeManagerDbContext CrearContexto(IActorAuditoria actor)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            // Mismo orden que en producción: auditoría primero, sellado después
            // (ver AuditoriaConIdentidadDualTests).
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ActorAuditoriaFalso(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class EvaluadorExpedienteNulo : CaeManager.Application.Visitas.Antelacion.IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}

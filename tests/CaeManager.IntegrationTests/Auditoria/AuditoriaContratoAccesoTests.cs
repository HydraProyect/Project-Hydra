using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Inbound.ContratosAcceso;
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
/// Plan backend Inbound § 7 (tarjeta 45): Renovar, Fijar condiciones, Cancelar y Finalizar un Contrato de acceso Inbound
/// dejan su acción de negocio en la fila de auditoría, la escribe el <see cref="AuditoriaInterceptor"/> a petición del
/// contrato (<see cref="IAccionAuditoriaPropia"/>), cabe en la columna y no sobrevive al guardado: una edición posterior
/// de la misma instancia vuelve a ser «Modificado».
/// </summary>
public class AuditoriaContratoAccesoTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly Guid _usuario = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Cada_accion_de_negocio_deja_su_fila_y_una_edicion_posterior_es_Modificado()
    {
        var hoy = DiaDeNegocio.Hoy();
        var (porEmpezar, empezado) = await SembrarContratosAsync(hoy);

        await using (var contexto = CrearContexto())
        {
            var contrato = await contexto.ContratosAcceso.SingleAsync(c => c.Id == porEmpezar);
            contrato.Renovar(hoy.AddDays(60), _usuario, DateTime.UtcNow);
            await contexto.SaveChangesAsync();

            // Misma instancia, otro guardado: ya no es una renovación.
            contrato.Actualizar(contrato.FechaFin, "Nota posterior", _usuario, DateTime.UtcNow);
            await contexto.SaveChangesAsync();

            contrato.FijarCondiciones(new CondicionesContratoAcceso(TipoContratoAcceso.Obra, PropiaActividadContrato.Si, 2, 5, null, "Aforo"));
            await contexto.SaveChangesAsync();

            contrato.Cancelar(hoy, tieneActividad: false);
            await contexto.SaveChangesAsync();

            var otro = await contexto.ContratosAcceso.SingleAsync(c => c.Id == empezado);
            otro.Finalizar(hoy);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var filas = await lectura.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(ContratoAcceso))
            .Select(r => new { r.EntidadId, r.Accion })
            .ToListAsync();

        var delPorEmpezar = filas.Where(f => f.EntidadId == porEmpezar).Select(f => f.Accion).ToList();
        delPorEmpezar.Should().ContainSingle(a => a == RegistroAuditoria.AccionRenovado);
        delPorEmpezar.Should().ContainSingle(a => a == RegistroAuditoria.AccionCondicionesFijadas);
        delPorEmpezar.Should().ContainSingle(a => a == RegistroAuditoria.AccionCancelado);
        delPorEmpezar.Count(a => a == "Modificado").Should().Be(1, "la edición de las notas tras renovar sigue siendo «Modificado»");
        filas.Where(f => f.EntidadId == empezado).Select(f => f.Accion)
            .Should().ContainSingle(a => a == RegistroAuditoria.AccionFinalizado);
    }

    private async Task<(Guid PorEmpezar, Guid Empezado)> SembrarContratosAsync(DateOnly hoy)
    {
        await using var contexto = CrearContexto();
        var cliente = Empresa.CrearComoCliente("Cliente empresarial auditado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var contratista = new Empresa("Empresa contratista auditada", "B10380202");
        var centro = new Centro(cliente.Id, cliente.Id, "Centro de trabajo auditado");
        contexto.Empresas.AddRange(cliente, contratista);
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        var porEmpezar = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(10), hoy.AddDays(40), null);
        var empezado = ContratoAcceso.Crear(cliente.Id, centro.Id, contratista.Id, hoy.AddDays(-10), null, null);
        contexto.ContratosAcceso.AddRange(porEmpezar, empezado);
        await contexto.SaveChangesAsync();
        return (porEmpezar.Id, empezado.Id);
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(
                new AuditoriaInterceptor(new ActorAuditoriaFalso(ActorAuditoria.Normal(_usuario))),
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

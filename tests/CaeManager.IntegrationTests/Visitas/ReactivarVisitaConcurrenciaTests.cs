using CaeManager.Application.Common;
using CaeManager.Application.Visitas.Commands.CancelarVisita;
using CaeManager.Application.Visitas.Commands.ReactivarVisita;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Interceptors;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// Reactivar una Visita comprueba la versión que el usuario vio (contrato de la ficha 09, el mismo de
/// «Deshacer» la anotación de vigencia) y es idempotente. Con Postgres real, los interceptores de producción
/// (incluido <see cref="ConcurrenciaOptimistaInterceptor"/>, sin el cual la versión no cambia nunca y el test
/// no prueba nada) y DOS contextos que hacen de dos circuitos de Blazor: el circuito A cancela y conserva la
/// Visita rastreada; el circuito B actúa después. Sin releer de la base de datos, A compararía contra su copia
/// vieja, pasaría la versión y chocaría en el UPDATE.
/// </summary>
public class ReactivarVisitaConcurrenciaTests : IAsyncLifetime
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
    public async Task Reactivar_con_la_version_vigente_reactiva_y_renueva_la_version()
    {
        var visitaId = await SembrarVisitaAsync();
        await using var circuitoA = CrearContexto();
        var recibo = (await CancelarAsync(circuitoA, visitaId, "Obra aplazada")).Valor;

        var resultado = await ReactivarAsync(circuitoA, visitaId, recibo.VersionResultante, "Se retoma");

        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Codigo : "");
        var (cancelada, _, version) = await LeerAsync(visitaId);
        cancelada.Should().BeFalse();
        version.Should().NotBe(recibo.VersionResultante, "control: el interceptor de concurrencia estaba registrado y la versión se renovó");
    }

    [Fact]
    public async Task Deshacer_tras_un_cambio_ajeno_en_otro_circuito_da_conflicto_y_no_pisa_la_nueva_cancelacion()
    {
        var visitaId = await SembrarVisitaAsync();
        await using var circuitoA = CrearContexto();
        var recibo = (await CancelarAsync(circuitoA, visitaId, "Primer motivo")).Valor;

        // Otra persona la reactiva y la vuelve a cancelar, con otro motivo.
        await using (var circuitoB = CrearContexto())
        {
            var actual = (await LeerAsync(visitaId)).Version;
            (await ReactivarAsync(circuitoB, visitaId, actual, null)).EsExitoso.Should().BeTrue("control positivo");
            (await CancelarAsync(circuitoB, visitaId, "Segundo motivo")).EsExitoso.Should().BeTrue("control positivo");
        }

        var deshacer = await ReactivarAsync(circuitoA, visitaId, recibo.VersionResultante);

        deshacer.EsFallido.Should().BeTrue();
        deshacer.Error.Codigo.Should().Be(ConcurrenciaOptimista.CodigoConflicto,
            "el conflicto sale del handler, no de una DbUpdateConcurrencyException");
        deshacer.Error.Mensaje.Should().Contain("cambió").And.Contain("recargado");
        var (cancelada, motivo, _) = await LeerAsync(visitaId);
        cancelada.Should().BeTrue("el cambio ajeno sigue en pie");
        motivo.Should().Be("Segundo motivo");
    }

    [Fact]
    public async Task Un_segundo_deshacer_sobre_una_visita_ya_reactivada_es_exito_sin_escribir_ni_auditar_otra_vez()
    {
        var visitaId = await SembrarVisitaAsync();
        await using var circuitoA = CrearContexto();
        var recibo = (await CancelarAsync(circuitoA, visitaId, "Obra aplazada")).Valor;

        await using (var circuitoB = CrearContexto())
            (await ReactivarAsync(circuitoB, visitaId, recibo.VersionResultante, "Motivo de B")).EsExitoso.Should().BeTrue("control positivo");
        var antes = await LeerAsync(visitaId);
        var filasAntes = await ContarAuditoriaAsync(visitaId);

        var segundo = await ReactivarAsync(circuitoA, visitaId, recibo.VersionResultante, "Motivo de A");

        segundo.EsExitoso.Should().BeTrue(segundo.EsFallido ? segundo.Error.Codigo : "");
        var despues = await LeerAsync(visitaId);
        despues.Should().Be(antes, "no se escribió nada: ni la versión ni la reactivación de B");
        (await ContarAuditoriaAsync(visitaId)).Should().Be(filasAntes);
    }

    // ── Composición ───────────────────────────────────────────────────────

    private static Task<Result<VisitaCanceladaDto>> CancelarAsync(CaeManagerDbContext contexto, Guid visitaId, string motivo) =>
        new CancelarVisitaCommandHandler(new VisitaRepository(contexto), contexto, new AlcanceDatosServiceFalso())
            .Handle(new CancelarVisitaCommand(visitaId, motivo), CancellationToken.None);

    private static Task<Result> ReactivarAsync(CaeManagerDbContext contexto, Guid visitaId, Guid version, string? motivo = null) =>
        new ReactivarVisitaCommandHandler(
                new VisitaRepository(contexto), contexto, new AlcanceDatosServiceFalso(), new EvaluadorExpedienteNulo(),
                NullLogger<ReactivarVisitaCommandHandler>.Instance)
            .Handle(new ReactivarVisitaCommand(visitaId, version, motivo), CancellationToken.None);

    private async Task<(bool Cancelada, string? Motivo, Guid Version)> LeerAsync(Guid visitaId)
    {
        await using var lectura = CrearContexto();
        var v = await lectura.Visitas.IgnoreQueryFilters().AsNoTracking().SingleAsync(x => x.Id == visitaId);
        return (v.EstaCancelada, v.MotivoCancelacion, v.Version);
    }

    private async Task<int> ContarAuditoriaAsync(Guid visitaId)
    {
        await using var lectura = CrearContexto();
        return await lectura.RegistrosAuditoria.CountAsync(r => r.EntidadTipo == nameof(Visita) && r.EntidadId == visitaId);
    }

    private async Task<Guid> SembrarVisitaAsync()
    {
        await using var contexto = CrearContexto();
        var titular = Empresa.CrearComoCliente("Titular concurrente", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null);
        var proveedora = new Empresa("Contratista concurrente", "B10380202");
        var centro = new Centro(titular.Id, proveedora.Id, "Almacén Norte");
        var hoy = DiaDeNegocio.Hoy();
        var visita = new Visita(centro.Id, hoy.AddDays(2), hoy.AddDays(3), null);
        contexto.Empresas.AddRange(titular, proveedora);
        contexto.Centros.Add(centro);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();
        return visita.Id;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var actor = new ActorAuditoriaFalso(ActorAuditoria.Normal(Guid.NewGuid()));
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual), new ConcurrenciaOptimistaInterceptor())
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

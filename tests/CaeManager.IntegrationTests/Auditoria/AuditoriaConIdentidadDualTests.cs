using CaeManager.Application.Common;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.Auditing;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// La escisión del canal de identidad (ADR-011 § 8.5, requisito 1): la
/// auditoría deja de firmar con "el usuario actual" a secas y pasa a registrar
/// <b>quién</b> figura como autor, <b>quién estaba realmente</b> detrás y
/// <b>por qué vía</b> operaba.
///
/// Hoy el actor real y el autor coinciden —no existe la impersonación—, así
/// que lo que estos tests fijan es que la separación existe y que la vía se
/// guarda de verdad. Es lo que permitirá después prohibir una sesión
/// privilegiada sin actor, que no se puede prohibir si no se sabe distinguir
/// "no lo sé" de "fue un acceso normal".
/// </summary>
public class AuditoriaConIdentidadDualTests : IAsyncLifetime
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
    public async Task Un_cambio_en_via_normal_registra_al_actor_como_autor_y_sin_via_de_acceso()
    {
        var usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.Normal(usuarioId))))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente("Cliente auditado", "B12345674", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeClienteAsync();

        registro.UsuarioId.Should().Be(usuarioId);
        registro.ActorRealUsuarioId.Should().Be(usuarioId, "sin impersonación, autor y actor real son el mismo");
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.Normal);
        registro.ViaAccesoId.Should().BeNull();
    }

    [Fact]
    public async Task Operar_un_workspace_delegado_deja_constancia_de_la_operacion_que_lo_ampara()
    {
        // Es la mitad de la pregunta que la auditoría no sabía responder: no
        // basta con "el usuario X tocó esto", hace falta saber que lo hizo
        // operando por delegación y bajo cuál.
        var usuarioId = Guid.NewGuid();
        var operacionId = Guid.NewGuid();

        var actor = new ActorAuditoria(usuarioId, null, TipoViaAcceso.OperacionDelegada, operacionId);

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(actor)))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Cliente delegado", "B58818501", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeClienteAsync();

        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.OperacionDelegada);
        registro.ViaAccesoId.Should().Be(operacionId);
    }

    [Fact]
    public async Task Una_impersonacion_registra_al_simulado_como_autor_y_conserva_al_actor_real()
    {
        // La capacidad todavía no existe, pero el contrato ya tiene que
        // soportarla: si el actor real se perdiera aquí, una acción hecha
        // simulando a alguien sería indistinguible de una suya, que es
        // exactamente lo que ADR-011 § 8.4 prohíbe.
        var administrador = Guid.NewGuid();
        var simulado = Guid.NewGuid();
        var sesionId = Guid.NewGuid();

        var actor = new ActorAuditoria(administrador, simulado, TipoViaAcceso.SesionPrivilegiada, sesionId);

        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(actor)))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Cliente impersonado", "B10380186", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeClienteAsync();

        registro.UsuarioId.Should().Be(simulado, "el autor visible es a quien se simula");
        registro.ActorRealUsuarioId.Should().Be(administrador, "y el administrador real no se pierde");
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.SesionPrivilegiada);
        registro.ViaAccesoId.Should().Be(sesionId);
    }

    [Fact]
    public async Task Sin_identidad_resuelta_la_via_queda_Desconocida_y_no_se_disfraza_de_normal()
    {
        // El guardado síncrono que no puede esperar a los claims, y los jobs
        // de fondo. Registrar "Normal sin usuario" sería mentir: no se sabe.
        await using (var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver)))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente("Cliente sin actor", "B10380194", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            await contexto.SaveChangesAsync();
        }

        var registro = await ObtenerRegistroDeClienteAsync();

        registro.UsuarioId.Should().BeNull();
        registro.ActorRealUsuarioId.Should().BeNull();
        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.Desconocida);
    }

    [Fact]
    public async Task El_guardado_sincrono_no_bloquea_y_audita_como_desconocida_si_la_identidad_no_esta_lista()
    {
        // Reproduce el motivo por el que la vía síncrona no puede esperar:
        // bloquear sobre un Task pendiente dentro de un circuito de Blazor
        // arriesga un interbloqueo. El registro sale igualmente, marcado.
        var actorLento = new ActorAuditoriaFalso(
            ActorAuditoria.Normal(Guid.NewGuid()), resueltoSincronamente: false);

        await using (var contexto = CrearContexto(actorLento))
        {
            contexto.Empresas.Add(Empresa.CrearComoCliente(
                "Cliente sincrono", "B10380202", esCritico: false, notas: null, ejecutivoUsuarioId: null));
            contexto.SaveChanges();
        }

        var registro = await ObtenerRegistroDeClienteAsync();

        registro.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.Desconocida);
        registro.ActorRealUsuarioId.Should().BeNull();
    }

    private async Task<RegistroAuditoria> ObtenerRegistroDeClienteAsync()
    {
        await using var contexto = CrearContexto(new ActorAuditoriaFalso(ActorAuditoria.SinResolver));

        return await contexto.RegistrosAuditoria
            .Where(r => r.EntidadTipo == nameof(Empresa))
            .OrderByDescending(r => r.FechaUtc)
            .FirstAsync();
    }

    private CaeManagerDbContext CrearContexto(IActorAuditoria actor)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            // El ORDEN importa y es el mismo que en producción (ver
            // InfrastructureServiceCollectionExtensions): auditoría primero,
            // sellado después. Al revés, las filas de auditoría que añade el
            // primer interceptor nacen sin TenantId y el filtro global las
            // esconde para siempre — no fallan, simplemente no se ven.
            .AddInterceptors(new AuditoriaInterceptor(actor), new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    /// <summary>
    /// <paramref name="resueltoSincronamente"/> distingue los dos caminos del
    /// interceptor: el asíncrono, que siempre puede esperar la identidad, y el
    /// síncrono, que solo la aprovecha si ya estaba lista.
    /// </summary>
    private sealed class ActorAuditoriaFalso(ActorAuditoria actor, bool resueltoSincronamente = true) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);

        public ActorAuditoria? ObtenerSiYaEstaResuelto() => resueltoSincronamente ? actor : null;
    }
}

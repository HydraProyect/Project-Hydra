using CaeManager.Application.Auditoria;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Queries.ObtenerAdjuntoParaDescarga;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>
/// Endpoint de descarga de adjuntos (Issue #18): nunca servir un archivo por
/// Guid sin comprobar que la conversación dueña es visible para quien lo
/// pide — ni por cartera de Cliente ni, si el hilo cuelga de un buzón
/// personal de un gestor, por propiedad de esa conexión (auditoría módulo 6).
/// </summary>
public class ObtenerAdjuntoParaDescargaQueryTests : IAsyncLifetime
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
    public async Task Devuelve_el_adjunto_de_una_conversacion_visible()
    {
        await using var contexto = CrearContexto();

        var cliente = Empresa.CrearComoCliente("Cliente Descarga Adjunto S.L.", "B10380194", false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        var conversacion = new Conversacion("Documentación recibida", cliente.Id);
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Adjunto el certificado");
        await contexto.SaveChangesAsync();

        var adjunto = new AdjuntoMensaje(mensaje.Id, "certificado.pdf", "application/pdf", 1024, "adjuntos/certificado.pdf");
        contexto.AdjuntosMensaje.Add(adjunto);
        await contexto.SaveChangesAsync();

        var handler = new ObtenerAdjuntoParaDescargaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(rol: "GestorCae"), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.ArchivoUrl.Should().Be("adjuntos/certificado.pdf");
    }

    [Fact]
    public async Task Un_adjunto_de_otro_tenant_no_es_visible()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerAdjuntoParaDescargaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(rol: "GestorCae"), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerAdjuntoParaDescargaQuery(Guid.NewGuid()), CancellationToken.None);

        resultado.Should().BeNull();
    }

    /// <summary>Auditoría módulo 6, hallazgo crítico: sin el eje de ConexionIntegracionId, cualquier gestor que adivinara el Guid podía descargar un adjunto del buzón personal de otro gestor.</summary>
    [Fact]
    public async Task Un_adjunto_de_un_buzon_personal_ajeno_no_es_visible()
    {
        await using var contexto = CrearContexto();

        var conversacion = new Conversacion("Documentación recibida en buzón personal");
        var conexionAjenaId = Guid.NewGuid();
        conversacion.AsociarConexion(conexionAjenaId, "hilo-externo-ajeno");
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Adjunto el certificado");
        await contexto.SaveChangesAsync();

        var adjunto = new AdjuntoMensaje(mensaje.Id, "certificado.pdf", "application/pdf", 1024, "adjuntos/certificado.pdf");
        contexto.AdjuntosMensaje.Add(adjunto);
        await contexto.SaveChangesAsync();

        var handler = new ObtenerAdjuntoParaDescargaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(conexionesIntegracionAjenas: [conexionAjenaId]),
            new CurrentUserServiceFalso(rol: "GestorCae"), new RegistroAccesoDatoSensibleFalso());

        var resultado = await handler.Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task La_descarga_de_un_adjunto_de_triage_deja_fila_con_el_Actor_real_y_la_via_en_el_Tenant_propietario()
    {
        await using var contexto = CrearContexto();

        var conversacion = new Conversacion("Sin clasificar");
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "desconocido@ejemplo.com", "Adjunto");
        await contexto.SaveChangesAsync();

        var adjunto = new AdjuntoMensaje(mensaje.Id, "contrato.pdf", "application/pdf", 2048, "adjuntos/contrato.pdf");
        contexto.AdjuntosMensaje.Add(adjunto);
        await contexto.SaveChangesAsync();

        var gestor = Guid.NewGuid();
        var asignacionOperacionId = Guid.NewGuid();
        var registro = new RegistroAccesoDatoSensibleService(
            new ActorFijo(new ActorAuditoria(gestor, null, TipoViaAcceso.OperacionDelegada, asignacionOperacionId)),
            new RegistroAccesoDatoSensibleRepository(contexto, NullLogger<RegistroAccesoDatoSensibleRepository>.Instance));
        var handler = new ObtenerAdjuntoParaDescargaQueryHandler(
            contexto, new AlcanceDatosServiceFalso(), new CurrentUserServiceFalso(rol: "GestorCae"), registro);

        (await handler.Handle(new ObtenerAdjuntoParaDescargaQuery(adjunto.Id), CancellationToken.None)).Should().NotBeNull();

        var fila = await contexto.RegistrosAuditoria.AsNoTracking()
            .SingleAsync(r => r.EntidadId == adjunto.Id && r.Accion == RegistroAuditoria.AccionAccesoDatoSensible);
        fila.EntidadTipo.Should().Be(nameof(AdjuntoMensaje));
        fila.TenantId.Should().Be(_tenant);
        fila.ActorRealUsuarioId.Should().Be(gestor);
        fila.ViaAcceso.Should().Be(TipoViaAccesoAuditoria.OperacionDelegada);
        fila.ViaAccesoId.Should().Be(asignacionOperacionId);
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

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }
}

using CaeManager.Application.Visitas.Antelacion;
using CaeManager.Application.Visitas.Commands.CrearVisita;
using CaeManager.Application.Visitas.Eventos;
using CaeManager.Application.Visitas.PaqueteDocumental;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using MediatR;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// docs/COMUNICACIONES.md § 16.7: "Eventos del sistema en el timeline —
/// Visitas + Documentos en v1". Cubre que crear una Visita desde una
/// SugerenciaVisitaCorreo publica VisitaCreadaEvent con la Conversacion de
/// origen — el resto de CrearVisitaCommandHandler (validaciones, paquete
/// documental) no tenía cobertura previa y queda fuera de este PR.
/// </summary>
public class CrearVisitaCommandTests : IAsyncLifetime
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
    public async Task Publica_el_evento_de_visita_creada_cuando_viene_de_una_sugerencia()
    {
        await using var contexto = CrearContexto();

        var cliente = Empresa.CrearComoCliente("Cliente Visita Guiada S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Visita Guiada S.L.", "B10380186");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Visita Guiada");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "12345678Z");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        var conversacion = new Conversacion("Solicitud de visita");
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, CanalConversacion.Correo, "cliente@ejemplo.com", "Necesitamos una visita mañana");
        await contexto.SaveChangesAsync();

        var sugerencia = new SugerenciaVisitaCorreo(mensaje.Id, centro.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), null, "Pide visita mañana", 90, 90, 90);
        contexto.SugerenciasVisitaCorreo.Add(sugerencia);
        await contexto.SaveChangesAsync();

        var publicador = new PublicadorDeMentira();
        var handler = new CrearVisitaCommandHandler(
            new VisitaRepository(contexto), new VisitaTrabajadorRepository(contexto),
            contexto, contexto,
            new SugerenciaVisitaCorreoRepository(contexto), contexto,
            new PaqueteDocumentalDeMentira(), new EvaluadorExpedienteDeMentira(), new CurrentUserServiceFalso(), publicador, contexto,
            NullLogger<CrearVisitaCommandHandler>.Instance);

        var comando = new CrearVisitaCommand(
            centro.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            [trabajador.Id], Notas: null, SugerenciaVisitaCorreoId: sugerencia.Id);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsFallido.Should().BeFalse();
        publicador.Publicados.Should().ContainSingle()
            .Which.Should().BeOfType<VisitaCreadaEvent>()
            .Which.Should().BeEquivalentTo(new VisitaCreadaEvent(conversacion.Id, resultado.Valor));
    }

    [Fact]
    public async Task No_publica_nada_cuando_la_visita_se_crea_sin_sugerencia()
    {
        await using var contexto = CrearContexto();

        var cliente = Empresa.CrearComoCliente("Cliente Visita Directa S.L.", "B10380194", false, null, null);
        var empresa = new Empresa("Empresa Visita Directa S.L.", "B10380186");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Visita Directa");
        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "87654321X");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        var publicador = new PublicadorDeMentira();
        var handler = new CrearVisitaCommandHandler(
            new VisitaRepository(contexto), new VisitaTrabajadorRepository(contexto),
            contexto, contexto,
            new SugerenciaVisitaCorreoRepository(contexto), contexto,
            new PaqueteDocumentalDeMentira(), new EvaluadorExpedienteDeMentira(), new CurrentUserServiceFalso(), publicador, contexto,
            NullLogger<CrearVisitaCommandHandler>.Instance);

        var comando = new CrearVisitaCommand(
            centro.Id, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)),
            [trabajador.Id], Notas: null);

        var resultado = await handler.Handle(comando, CancellationToken.None);

        resultado.EsFallido.Should().BeFalse();
        publicador.Publicados.Should().BeEmpty();
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

    private class PaqueteDocumentalDeMentira : IPaqueteDocumentalVisitaService
    {
        public Task GenerarYEnviarAsync(Guid visitaId, Guid conversacionId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private class EvaluadorExpedienteDeMentira : IEvaluadorExpedienteVisitaService
    {
        public Task<bool> EvaluarAsync(Guid visitaId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task EvaluarPorDocumentoAsync(Guid documentoId, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private class PublicadorDeMentira : IPublisher
    {
        public List<INotification> Publicados { get; } = [];

        public Task Publish(object notification, CancellationToken cancellationToken = default)
        {
            Publicados.Add((INotification)notification);
            return Task.CompletedTask;
        }

        public Task Publish<TNotification>(TNotification notification, CancellationToken cancellationToken = default)
            where TNotification : INotification
        {
            Publicados.Add(notification);
            return Task.CompletedTask;
        }
    }
}

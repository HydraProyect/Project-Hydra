using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Reclamaciones;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>docs/COMUNICACIONES.md § 12.3/§ 16.7 — eventos del sistema mezclados con los mensajes del hilo.</summary>
public class ObtenerConversacionPorIdQueryEventosTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private readonly AlcanceDatosServiceFalso _alcanceDatos = new();
    private readonly CurrentUserServiceFalso _currentUser = new(rol: "GestorCae");

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Devuelve_el_evento_de_visita_creada_con_su_descripcion()
    {
        Guid conversacionId, visitaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Timeline S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Timeline S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var centro = new Centro(cliente.Id, empresa.Id, "Centro Timeline");
            contexto.Centros.Add(centro);
            await contexto.SaveChangesAsync();

            var conversacion = new Conversacion("Coordinación de visita");
            conversacion.AsignarCliente(cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;

            var visita = new Visita(centro.Id, new DateOnly(2026, 8, 17), new DateOnly(2026, 8, 17), null, OrigenVisita.Correo);
            contexto.Visitas.Add(visita);
            await contexto.SaveChangesAsync();
            visitaId = visita.Id;

            contexto.EventosConversacion.Add(new EventoConversacion(conversacionId, TipoEventoConversacion.VisitaCreada, visitaId, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura, lectura, _alcanceDatos, new GanssSanitizadorHtmlService(), _currentUser,
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(lectura)));

        var detalle = await handler.Handle(new ObtenerConversacionPorIdQuery(conversacionId), CancellationToken.None);

        detalle.Should().NotBeNull();
        detalle!.Eventos.Should().ContainSingle();
        var evento = detalle.Eventos[0];
        evento.Tipo.Should().Be(TipoEventoConversacion.VisitaCreada);
        evento.ReferenciaId.Should().Be(visitaId);
        evento.Descripcion.Should().Contain("Centro Timeline").And.Contain("17/08/2026");
    }

    [Fact]
    public async Task Devuelve_el_evento_de_documento_actualizado_con_su_descripcion()
    {
        Guid conversacionId, documentoId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Timeline Documento S.L.", "B10380194", false, null, null);
            var empresa = new Empresa("Empresa Timeline Documento S.L.", "B10380186");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Elena", "Soto", "11223344B");
            var tipoDocumento = new TipoDocumento("Certificado de formación", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var conversacion = new Conversacion("Documentación recibida");
            conversacion.AsignarCliente(cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;

            var documento = Documento.DeTrabajador(trabajador.Id, tipoDocumento.Id, new DateOnly(2026, 8, 1), new DateOnly(2027, 8, 1));
            contexto.Documentos.Add(documento);
            await contexto.SaveChangesAsync();
            documentoId = documento.Id;

            contexto.EventosConversacion.Add(new EventoConversacion(conversacionId, TipoEventoConversacion.DocumentoActualizado, documentoId, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura, lectura, _alcanceDatos, new GanssSanitizadorHtmlService(), _currentUser,
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(lectura)));

        var detalle = await handler.Handle(new ObtenerConversacionPorIdQuery(conversacionId), CancellationToken.None);

        detalle.Should().NotBeNull();
        detalle!.Eventos.Should().ContainSingle();
        var evento = detalle.Eventos[0];
        evento.Tipo.Should().Be(TipoEventoConversacion.DocumentoActualizado);
        evento.ReferenciaId.Should().Be(documentoId);
        evento.Descripcion.Should().Contain("Certificado de formación").And.Contain("Elena Soto");
    }

    [Fact]
    public async Task Devuelve_el_evento_de_reclamacion_enviada_con_el_numero_de_documentos()
    {
        Guid conversacionId, reclamacionId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Timeline Reclamación S.L.", "B10380186", false, null, null);
            var empresa = new Empresa("Empresa Timeline Reclamación S.L.", "B10380194");
            contexto.Empresas.Add(cliente);
            contexto.Empresas.Add(empresa);
            await contexto.SaveChangesAsync();

            var trabajador = Trabajador.DeEmpresa(empresa.Id, "Marco", "Rivas", "11223344B");
            var tipoDocumento = new TipoDocumento("Reconocimiento médico", 12, true, 1, AmbitoAplicacion.Trabajador);
            contexto.Trabajadores.Add(trabajador);
            contexto.TiposDocumento.Add(tipoDocumento);
            await contexto.SaveChangesAsync();

            var primero = Documento.DeTrabajador(trabajador.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 9, 1));
            var segundo = Documento.DeTrabajador(trabajador.Id, tipoDocumento.Id, new DateOnly(2026, 1, 1), new DateOnly(2026, 10, 1));
            contexto.Documentos.AddRange(primero, segundo);

            var conversacion = new Conversacion("Documentación pendiente", cliente.Id);
            contexto.Conversaciones.Add(conversacion);
            await contexto.SaveChangesAsync();
            conversacionId = conversacion.Id;

            var reclamacion = new ReclamacionDocumental(
                cliente.Id, Guid.NewGuid(), "portal@cliente.local", DateTime.UtcNow, [primero.Id, segundo.Id], conversacionId);
            contexto.ReclamacionesDocumentales.Add(reclamacion);
            await contexto.SaveChangesAsync();
            reclamacionId = reclamacion.Id;

            contexto.EventosConversacion.Add(new EventoConversacion(
                conversacionId, TipoEventoConversacion.ReclamacionEnviada, reclamacionId, DateTime.UtcNow));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerConversacionPorIdQueryHandler(
            lectura, lectura, lectura, lectura, lectura, lectura, lectura, lectura, _alcanceDatos, new GanssSanitizadorHtmlService(), _currentUser,
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(lectura)));

        var detalle = await handler.Handle(new ObtenerConversacionPorIdQuery(conversacionId), CancellationToken.None);

        detalle.Should().NotBeNull();
        var evento = detalle!.Eventos.Should().ContainSingle().Which;
        evento.Tipo.Should().Be(TipoEventoConversacion.ReclamacionEnviada);
        evento.ReferenciaId.Should().Be(reclamacionId);
        evento.Descripcion.Should().Be("Se reclamaron 2 documentos pendientes por esta conversación.");
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new Microsoft.AspNetCore.DataProtection.EphemeralDataProtectionProvider(), tenantActual);
    }
}

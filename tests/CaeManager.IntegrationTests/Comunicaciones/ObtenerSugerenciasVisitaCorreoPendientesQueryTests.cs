using CaeManager.Application.Comunicaciones.Queries.ObtenerSugerenciasVisitaCorreoPendientes;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Comunicaciones;

/// <summary>Fase F: fuente de "visita sorpresa" para la Bandeja del gestor.</summary>
public class ObtenerSugerenciasVisitaCorreoPendientesQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroVisibleId;
    private Guid _centroAjenoId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Cliente Sugerencia S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Empresa Sugerencia S.L.", "B87654323");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centroVisible = new Centro(cliente.Id, empresa.Id, "Centro visible");
        var centroAjeno = new Centro(cliente.Id, empresa.Id, "Centro ajeno a la cartera");
        contexto.Centros.AddRange(centroVisible, centroAjeno);
        await contexto.SaveChangesAsync();

        _centroVisibleId = centroVisible.Id;
        _centroAjenoId = centroAjeno.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    private async Task<Guid> SembrarSugerenciaAsync(Guid? centroId, bool resuelta, CanalConversacion canal = CanalConversacion.Correo)
    {
        await using var contexto = CrearContexto();

        var conversacion = canal == CanalConversacion.WhatsApp
            ? Conversacion.CrearWhatsApp("+34600000000", Guid.NewGuid(), clienteId: null, ejecutivoId: null)
            : new Conversacion("Solicitud de entrada");
        contexto.Conversaciones.Add(conversacion);
        await contexto.SaveChangesAsync();

        var mensaje = conversacion.AgregarMensaje(DireccionMensaje.Entrante, "cliente@ejemplo.com", "Necesitamos entrar mañana");
        await contexto.SaveChangesAsync();

        var sugerencia = new SugerenciaVisitaCorreo(mensaje.Id, centroId, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)), null, "Pide entrar mañana");
        if (resuelta) sugerencia.Resolver();
        contexto.SugerenciasVisitaCorreo.Add(sugerencia);
        await contexto.SaveChangesAsync();

        return sugerencia.Id;
    }

    [Fact]
    public async Task Solo_devuelve_sugerencias_sin_resolver()
    {
        var pendienteId = await SembrarSugerenciaAsync(_centroVisibleId, resuelta: false);
        await SembrarSugerenciaAsync(_centroVisibleId, resuelta: true);

        await using var lectura = CrearContexto();
        var handler = new ObtenerSugerenciasVisitaCorreoPendientesQueryHandler(lectura, lectura, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(s => s.Id == pendienteId);
    }

    [Fact]
    public async Task Respeta_el_alcance_de_cartera_por_centro()
    {
        await SembrarSugerenciaAsync(_centroAjenoId, resuelta: false);

        await using var lectura = CrearContexto();
        var handler = new ObtenerSugerenciasVisitaCorreoPendientesQueryHandler(
            lectura, lectura, new AlcanceDatosServiceFalso(centroIds: [_centroVisibleId]));

        var resultado = await handler.Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        resultado.Should().BeEmpty();
    }

    [Fact]
    public async Task Trae_el_canal_de_la_conversacion_de_origen()
    {
        var whatsappId = await SembrarSugerenciaAsync(_centroVisibleId, resuelta: false, canal: CanalConversacion.WhatsApp);

        await using var lectura = CrearContexto();
        var handler = new ObtenerSugerenciasVisitaCorreoPendientesQueryHandler(lectura, lectura, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerSugerenciasVisitaCorreoPendientesQuery(), CancellationToken.None);

        resultado.Should().ContainSingle(s => s.Id == whatsappId).Subject.Canal.Should().Be(CanalConversacion.WhatsApp);
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

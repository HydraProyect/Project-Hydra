using CaeManager.Application.Comunicaciones;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.AplicarMacro;
using CaeManager.Application.Comunicaciones.Queries.ObtenerConversacionPorId;
using CaeManager.Application.Comunicaciones.Matching;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Repositories;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.Infrastructure.Comunicaciones;
using CaeManager.IntegrationTests.Reclamaciones;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace CaeManager.IntegrationTests.Comunicaciones;

public class PlantillasRespuestaTests : IAsyncLifetime
{
    private readonly string _cadena = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    public Task InitializeAsync() => Task.CompletedTask;
    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadena);

    [Fact]
    public async Task Aplicar_persiste_el_evento_y_la_lectura_del_hilo_identifica_la_plantilla()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        using var ambito = AmbitoTenantExplicito.Establecer(Guid.NewGuid());
        var empresa = new CaeManager.Domain.Empresas.Empresa("Empresa destinataria");
        contexto.Empresas.Add(empresa);
        var conversacion = new Conversacion("Documentación", clienteId: empresa.Id);
        var plantilla = new MacroRespuesta("Solicitud mensual", "Hola {{Cliente empresarial}}");
        contexto.Conversaciones.Add(conversacion);
        contexto.MacrosRespuesta.Add(plantilla);
        await contexto.SaveChangesAsync();
        var alcance = new AlcanceDatosServiceFalso();
        var aplicar = new AplicarMacroCommandHandler(contexto,
            new RellenoMacroRespuesta(contexto, contexto, contexto, contexto, contexto, PendientesDeReclamacionFabrica.Crear(contexto, alcance), alcance),
            alcance, new EventoConversacionRepository(contexto), contexto);

        var resultado = await aplicar.Handle(new(conversacion.Id, plantilla.Id), CancellationToken.None);
        resultado.Valor.Should().Be("Hola Empresa destinataria");
        contexto.ChangeTracker.Clear();
        var lectura = new ObtenerConversacionPorIdQueryHandler(contexto, contexto, contexto, contexto, contexto, contexto,
            contexto, contexto, alcance, new GanssSanitizadorHtmlService(), new CurrentUserServiceFalso(rol: "GestorCae"),
            new MotorCoincidenciaConversacionesService(new ConversacionRepository(contexto)));
        var detalle = await lectura.Handle(new(conversacion.Id), CancellationToken.None);
        detalle.Should().NotBeNull();
        detalle!.Eventos.Should().ContainSingle().Which.Descripcion.Should().Be("Se aplicó la plantilla Solicitud mensual al borrador.");
        detalle.Mensajes.Should().BeEmpty();

        using var otroTenant = AmbitoTenantExplicito.Establecer(Guid.NewGuid());
        var inaccesible = await aplicar.Handle(new(conversacion.Id, plantilla.Id), CancellationToken.None);
        inaccesible.Error.Codigo.Should().Be("Conversacion.NoEncontrada");
    }

    [Fact]
    public async Task La_siembra_es_por_Tenant_propietario_y_no_repone_ediciones_ni_eliminaciones()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        var tenantA = new Tenant("Plantillas A");
        var tenantB = new Tenant("Plantillas B");
        contexto.Tenants.AddRange(tenantA, tenantB);
        await contexto.SaveChangesAsync();
        await PlantillasRespuestaSeeder.SeedAsync(contexto);

        using (AmbitoTenantExplicito.Establecer(tenantA.Id))
        {
            var plantillas = await contexto.MacrosRespuesta.OrderBy(p => p.Titulo).ToListAsync();
            plantillas.Should().HaveCount(9);
            plantillas[0].Actualizar("Contenido propio", "Texto revisado por el equipo", null);
            plantillas[1].MarcarComoEliminado(Guid.NewGuid());
            await contexto.SaveChangesAsync();
        }
        await PlantillasRespuestaSeeder.SeedAsync(contexto);
        contexto.ChangeTracker.Clear();

        using (AmbitoTenantExplicito.Establecer(tenantA.Id))
        {
            var plantillas = await contexto.MacrosRespuesta.ToListAsync();
            plantillas.Should().HaveCount(8);
            plantillas.Should().Contain(p => p.Titulo == "Contenido propio" && p.CuerpoHtml == "Texto revisado por el equipo");
            plantillas.Should().OnlyContain(p => p.TenantId == tenantA.Id);
        }
        using (AmbitoTenantExplicito.Establecer(tenantB.Id))
        {
            var plantillas = await contexto.MacrosRespuesta.ToListAsync();
            plantillas.Should().HaveCount(9).And.OnlyContain(p => p.TenantId == tenantB.Id);
            plantillas.Should().NotContain(p => p.Titulo == "Contenido propio");
        }
    }

    [Fact]
    public async Task El_alta_de_un_Tenant_propietario_incluye_sus_nueve_plantillas()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        var tenant = new Tenant("Alta con plantillas");
        using (AmbitoTenantExplicito.Establecer(tenant.Id))
        {
            new TenantRepository(contexto).Agregar(tenant);
            await contexto.SaveChangesAsync();
            var plantillas = await contexto.MacrosRespuesta.ToListAsync();
            plantillas.Should().HaveCount(9).And.OnlyContain(p => p.TenantId == tenant.Id);
            plantillas.Select(p => p.Id).Should().BeEquivalentTo(PlantillasRespuestaPredefinidas.Crear(tenant.Id).Select(p => p.Id));
        }
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantDelAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadena, n => n.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual)).Options;
        return new(opciones, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class TenantDelAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }
}

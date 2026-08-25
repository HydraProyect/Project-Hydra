using CaeManager.Application.Visitas.Queries.ObtenerVisitas;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Configuracion;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// Fase F: NivelUrgencia y el filtro SoloUrgentes de ObtenerVisitasQuery,
/// contra PostgreSQL real — la parte de CalculadoraUrgenciaVisita que no se
/// puede probar sin una base de datos es el filtro SQL de SoloUrgentes
/// (ver el comentario del handler), que tiene que coincidir con lo que la
/// calculadora decidiría en memoria.
/// </summary>
public class ObtenerVisitasQueryUrgenciaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroId;
    private Guid _clienteId;
    private Guid _empresaId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        contexto.ParametrosSistema.Add(new ParametroSistema(30, 15, horasAvisoVisita: 48, horasCriticasVisita: 24));

        var cliente = Empresa.CrearComoCliente("Cliente Urgencia Visita S.L.", "B12345674", false, null, null);
        var empresa = new Empresa("Empresa Urgencia Visita S.L.", "B87654323");
        contexto.Empresas.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Urgencia Visita");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        _centroId = centro.Id;
        _clienteId = cliente.Id;
        _empresaId = empresa.Id;
    }

    public async Task DisposeAsync()
    {
        await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);
    }

    [Fact]
    public async Task Una_visita_que_empieza_manana_se_marca_como_critica()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var contexto = CrearContexto())
        {
            contexto.Visitas.Add(new Visita(_centroId, hoy.AddDays(1), hoy.AddDays(1), notas: null));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearHandler(lectura);

        var resultado = await handler.Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null),
            CancellationToken.None);

        resultado.Elementos.Should().ContainSingle(v => v.NivelUrgencia == NivelUrgenciaVisita.Critica);
    }

    [Fact]
    public async Task SoloUrgentes_excluye_una_visita_lejana_en_el_tiempo()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var contexto = CrearContexto())
        {
            contexto.Visitas.Add(new Visita(_centroId, hoy.AddDays(1), hoy.AddDays(1), notas: null));
            contexto.Visitas.Add(new Visita(_centroId, hoy.AddDays(10), hoy.AddDays(10), notas: null));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearHandler(lectura);

        var resultado = await handler.Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null, SoloUrgentes: true),
            CancellationToken.None);

        resultado.Elementos.Should().ContainSingle();
        resultado.Elementos[0].FechaInicio.Should().Be(hoy.AddDays(1));
    }

    [Fact]
    public async Task SoloUrgentes_incluye_una_visita_ya_en_curso()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        await using (var contexto = CrearContexto())
        {
            contexto.Visitas.Add(new Visita(_centroId, hoy.AddDays(-1), hoy.AddDays(1), notas: null));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = CrearHandler(lectura);

        var resultado = await handler.Handle(
            new ObtenerVisitasQuery(Busqueda: null, SoloActivas: true, NotificadoCliente: null, SoloUrgentes: true),
            CancellationToken.None);

        resultado.Elementos.Should().ContainSingle();
        resultado.Elementos[0].NivelUrgencia.Should().Be(NivelUrgenciaVisita.EnCurso);
    }

    private static ObtenerVisitasQueryHandler CrearHandler(CaeManagerDbContext contexto) =>
        new(contexto, contexto, contexto, contexto, contexto, contexto, new AlcanceDatosServiceFalso());

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

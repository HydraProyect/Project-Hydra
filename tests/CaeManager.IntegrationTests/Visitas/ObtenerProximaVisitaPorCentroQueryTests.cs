using CaeManager.Application.Visitas.Queries.ObtenerProximaVisitaPorCentro;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Trabajadores;
using CaeManager.Domain.Visitas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Visitas;

/// <summary>
/// Alimenta el badge "Visita dd/mm–dd/mm" de Centro 360 (docs/blueprints/CENTRO-360.md § 3.7)
/// y su ventana de contexto — pendiente hasta ahora: el recuento de trabajadores
/// por visita, tanto con una sola visita como con varias sin fusionar (DDL-035).
/// </summary>
public class ObtenerProximaVisitaPorCentroQueryTests : IAsyncLifetime
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
    public async Task Devuelve_el_recuento_de_trabajadores_de_una_visita_unica()
    {
        await using var contexto = CrearContexto();

        var cliente = new Cliente("Cliente Recuento Visita S.L.", "B10380194", esCritico: false);
        var empresa = new Empresa("Empresa Recuento Visita S.L.", "B10380186");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Recuento Visita");
        var trabajador1 = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "11111111H");
        var trabajador2 = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "22222222J");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.AddRange(trabajador1, trabajador2);
        await contexto.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var visita = new Visita(centro.Id, hoy.AddDays(1), hoy.AddDays(2), notas: null);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();

        contexto.VisitasTrabajadores.AddRange(
            new VisitaTrabajador(visita.Id, trabajador1.Id),
            new VisitaTrabajador(visita.Id, trabajador2.Id));
        await contexto.SaveChangesAsync();

        var handler = new ObtenerProximaVisitaPorCentroQueryHandler(contexto);
        var resultado = await handler.Handle(new ObtenerProximaVisitaPorCentroQuery([centro.Id]), CancellationToken.None);

        resultado.Should().ContainKey(centro.Id);
        resultado[centro.Id].Should().ContainSingle();
        resultado[centro.Id][0].TrabajadoresCount.Should().Be(2);
    }

    [Fact]
    public async Task Cada_visita_sin_fusionar_lleva_su_propio_recuento_independiente()
    {
        await using var contexto = CrearContexto();

        var cliente = new Cliente("Cliente Recuento Multivisita S.L.", "B10380194", esCritico: false);
        var empresa = new Empresa("Empresa Recuento Multivisita S.L.", "B10380186");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Recuento Multivisita");
        var trabajador1 = Trabajador.DeEmpresa(empresa.Id, "Ana", "García", "33333333P");
        var trabajador2 = Trabajador.DeEmpresa(empresa.Id, "Luis", "Pérez", "44444444A");
        var trabajador3 = Trabajador.DeEmpresa(empresa.Id, "Eva", "Ruiz", "55555555K");
        contexto.Centros.Add(centro);
        contexto.Trabajadores.AddRange(trabajador1, trabajador2, trabajador3);
        await contexto.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var visitaCorta = new Visita(centro.Id, hoy.AddDays(1), hoy.AddDays(2), notas: null);
        var visitaLarga = new Visita(centro.Id, hoy.AddDays(10), hoy.AddDays(12), notas: null);
        contexto.Visitas.AddRange(visitaCorta, visitaLarga);
        await contexto.SaveChangesAsync();

        contexto.VisitasTrabajadores.AddRange(
            new VisitaTrabajador(visitaCorta.Id, trabajador1.Id),
            new VisitaTrabajador(visitaLarga.Id, trabajador2.Id),
            new VisitaTrabajador(visitaLarga.Id, trabajador3.Id));
        await contexto.SaveChangesAsync();

        var handler = new ObtenerProximaVisitaPorCentroQueryHandler(contexto);
        var resultado = await handler.Handle(new ObtenerProximaVisitaPorCentroQuery([centro.Id]), CancellationToken.None);

        var visitas = resultado[centro.Id];
        visitas.Should().HaveCount(2);
        visitas.Single(v => v.VisitaId == visitaCorta.Id).TrabajadoresCount.Should().Be(1);
        visitas.Single(v => v.VisitaId == visitaLarga.Id).TrabajadoresCount.Should().Be(2);
    }

    [Fact]
    public async Task Una_visita_sin_trabajadores_devuelve_recuento_cero()
    {
        await using var contexto = CrearContexto();

        var cliente = new Cliente("Cliente Recuento Vacio S.L.", "B10380194", esCritico: false);
        var empresa = new Empresa("Empresa Recuento Vacio S.L.", "B10380186");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, empresa.Id, "Centro Recuento Vacio");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var visita = new Visita(centro.Id, hoy.AddDays(1), hoy.AddDays(2), notas: null);
        contexto.Visitas.Add(visita);
        await contexto.SaveChangesAsync();

        var handler = new ObtenerProximaVisitaPorCentroQueryHandler(contexto);
        var resultado = await handler.Handle(new ObtenerProximaVisitaPorCentroQuery([centro.Id]), CancellationToken.None);

        resultado[centro.Id][0].TrabajadoresCount.Should().Be(0);
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

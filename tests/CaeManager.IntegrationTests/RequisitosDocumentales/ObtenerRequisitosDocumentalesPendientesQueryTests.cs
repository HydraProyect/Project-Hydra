using CaeManager.Application.RequisitosDocumentales.Queries.ObtenerRequisitosDocumentalesPendientes;
using CaeManager.Domain.Clientes;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RequisitosDocumentales;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Centro = CaeManager.Domain.Centros.Centro;

namespace CaeManager.IntegrationTests.RequisitosDocumentales;

/// <summary>
/// Fase C (Bandeja del gestor): un RequisitoDocumental sin cumplir no
/// aparece en <c>/alertas</c> (ver el comentario de dominio en
/// RequisitoDocumental.cs) — esta Query es el primer punto de entrada que
/// sí lo expone, replicando el mismo filtro que ya usa
/// CalculoEstadoCentroService para bloquear un Centro.
/// </summary>
public class ObtenerRequisitosDocumentalesPendientesQueryTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centro1Id;
    private Guid _centro2Id;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var cliente = new Cliente("Bandeja de Requisitos S.L.", "B12345674", esCritico: false);
        var empresa = new Empresa("Empresa de Requisitos S.L.", "B87654323");
        contexto.Clientes.Add(cliente);
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var centro1 = new Centro(cliente.Id, empresa.Id, "Centro con requisito bloqueante");
        var centro2 = new Centro(cliente.Id, empresa.Id, "Centro sin requisitos pendientes");
        contexto.Centros.AddRange(centro1, centro2);
        await contexto.SaveChangesAsync();

        _centro1Id = centro1.Id;
        _centro2Id = centro2.Id;

        // Bloqueante y sin cumplir: debe aparecer.
        contexto.RequisitosDocumentales.Add(new RequisitoDocumental(centro1.Id, "PSS firmado", null, bloqueaAcceso: true));

        // Bloqueante pero ya cumplido: no debe aparecer.
        var cumplido = new RequisitoDocumental(centro2.Id, "Curso de riesgos", null, bloqueaAcceso: true);
        cumplido.MarcarCumplido(DateOnly.FromDateTime(DateTime.UtcNow));
        contexto.RequisitosDocumentales.Add(cumplido);

        // Sin cumplir pero no bloqueante: no debe aparecer (Fase C solo
        // cubre lo que compite en urgencia con un documento vencido).
        contexto.RequisitosDocumentales.Add(new RequisitoDocumental(centro2.Id, "Nota informativa", null, bloqueaAcceso: false));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Solo_devuelve_requisitos_que_bloquean_el_acceso_y_siguen_sin_cumplir()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerRequisitosDocumentalesPendientesQueryHandler(contexto, contexto, new AlcanceDatosServiceFalso());

        var resultado = await handler.Handle(new ObtenerRequisitosDocumentalesPendientesQuery(), CancellationToken.None);

        var pendiente = resultado.Should().ContainSingle().Subject;
        pendiente.CentroId.Should().Be(_centro1Id);
        pendiente.Descripcion.Should().Be("PSS firmado");
    }

    [Fact]
    public async Task Respeta_el_alcance_de_cartera_del_usuario()
    {
        await using var contexto = CrearContexto();
        var handler = new ObtenerRequisitosDocumentalesPendientesQueryHandler(
            contexto, contexto, new AlcanceDatosServiceFalso(centroIds: [_centro2Id]));

        var resultado = await handler.Handle(new ObtenerRequisitosDocumentalesPendientesQuery(), CancellationToken.None);

        resultado.Should().BeEmpty();
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

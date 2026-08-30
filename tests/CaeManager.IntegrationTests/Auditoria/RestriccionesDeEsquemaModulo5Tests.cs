using CaeManager.Domain.Empresas;
using CaeManager.Domain.Proyectos;
using CaeManager.Domain.Trabajadores;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;
using Centro = CaeManager.Domain.Centros.Centro;

namespace CaeManager.IntegrationTests.Auditoria;

/// <summary>
/// Auditoría Módulo 5, huecos arquitectónicos: tres invariantes que antes
/// solo comprobaba el código de aplicación pasan a ser irrepresentables en la
/// base de datos. Cada test intenta escribir directamente la fila inválida
/// (sin pasar por el comando, que ya la rechazaría antes) para demostrar que
/// ahora es la propia base la que la rechaza.
/// </summary>
public class RestriccionesDeEsquemaModulo5Tests : IAsyncLifetime
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
    public async Task Un_trabajador_no_puede_quedar_sin_empresa_ni_subcontrata()
    {
        await using var contexto = CrearContexto();
        var empresa = new Empresa("Empresa CHECK S.L.", "B87654323");
        contexto.Empresas.Add(empresa);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Sin", "Empleador", "77189989B");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        // Directo a la columna, esquivando el dominio (que nunca deja las dos
        // a null): CK_Trabajadores_EmpresaXorSubcontrata tiene que rechazarlo.
        await contexto.Invoking(c => c.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Trabajadores" SET "EmpresaId" = NULL WHERE "Id" = {trabajador.Id}"""))
            .Should().ThrowAsync<Npgsql.PostgresException>();
    }

    [Fact]
    public async Task Un_trabajador_no_puede_tener_empresa_y_subcontrata_a_la_vez()
    {
        await using var contexto = CrearContexto();
        var empresa = new Empresa("Empresa CHECK Dos S.L.", "B87654323");
        var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata CHECK S.L.", "B10380186", "Gestionada");
        contexto.Empresas.AddRange(empresa, subcontrata);
        await contexto.SaveChangesAsync();

        var trabajador = Trabajador.DeEmpresa(empresa.Id, "Doble", "Empleador", "77189989B");
        contexto.Trabajadores.Add(trabajador);
        await contexto.SaveChangesAsync();

        await contexto.Invoking(c => c.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Trabajadores" SET "SubcontrataId" = {subcontrata.Id} WHERE "Id" = {trabajador.Id}"""))
            .Should().ThrowAsync<Npgsql.PostgresException>();
    }

    [Fact]
    public async Task Un_proyecto_no_puede_apuntar_a_un_centro_de_otro_cliente()
    {
        await using var contexto = CrearContexto();
        var clienteReal = Empresa.CrearComoCliente("Cliente Real FK S.A.", "B12345674", false, null, null);
        var clienteAjeno = Empresa.CrearComoCliente("Cliente Ajeno FK S.A.", "B10380186", false, null, null);
        var empresaContratista = new Empresa("Contratista FK S.L.", "B87654323");
        contexto.Empresas.AddRange(clienteReal, clienteAjeno, empresaContratista);
        await contexto.SaveChangesAsync();

        var centro = new Centro(clienteReal.Id, empresaContratista.Id, "Centro FK");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        var proyecto = Proyecto.Crear(
            clienteReal.Id, centro.Id, "Proyecto FK", new DateOnly(2026, 1, 1), null, null);
        contexto.Proyectos.Add(proyecto);
        await contexto.SaveChangesAsync();

        // Directo a la columna: FK_Proyectos_Centros_TenantId_CentroId_ClienteId
        // tiene que rechazar que el proyecto pase a apuntar a un cliente
        // distinto del que realmente tiene el centro.
        await contexto.Invoking(c => c.Database.ExecuteSqlInterpolatedAsync(
                $"""UPDATE "Proyectos" SET "ClienteId" = {clienteAjeno.Id} WHERE "Id" = {proyecto.Id}"""))
            .Should().ThrowAsync<Npgsql.PostgresException>();
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

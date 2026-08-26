using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Subcontratas;

/// <summary>
/// F4.2b (2026-08-27): <c>ObtenerSubcontrataPorIdQuery</c> y
/// <c>ObtenerSubcontratasDeClienteQuery</c> repuntados de las tablas puente
/// legacy a <c>RelacionesEmpresariales</c>. A diferencia de otros lectores
/// migrados, aquí el JOIN discriminador contra <c>Empresa.EsPropia</c>/
/// <c>EsCritico</c>/<c>NivelServicio</c> no es defensa en profundidad: una
/// Subcontrata con Clientes Y Empresas propias a la vez, o un Cliente con
/// una Empresa propia Y una Subcontrata sirviéndole a la vez, son la
/// situación normal en cualquier tenant con actividad real — no un caso
/// límite bajo un rol privilegiado. Sin el discriminador, estos tests
/// fallarían con el primer par de datos realistas, no solo bajo ataque.
/// Verificado por una revisión adversarial independiente antes de
/// implementar (convergencia pre-cliente, 2026-08-27) antes de escribir
/// estos tests.
/// </summary>
public class F42bRelacionEmpresarialDiscriminadorTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task ObtenerSubcontrataPorIdQuery_separa_ClienteIds_y_EmpresaIds_cuando_ambos_coexisten()
    {
        Guid subcontrataId, clienteRealId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Mixta S.L.", "B10380186", NivelServicioSubcontrata.Gestionada.ToString());
            var clienteReal = Empresa.CrearComoCliente("Cliente Real De La Subcontrata S.A.", "B10380194", false, null, null);
            var empresaPropia = new Empresa("Empresa Propia Servida S.L.", "B10380202");
            contexto.Empresas.AddRange(subcontrata, clienteReal, empresaPropia);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id; clienteRealId = clienteReal.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            // Shape Subcontrata→Cliente (target real = Cliente) y Subcontrata→Empresa
            // (target real = Empresa propia) sobre la MISMA Subcontrata a la vez.
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteRealId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrataId, empresaPropiaId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontrataPorIdQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSubcontrataPorIdQuery(subcontrataId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.ClienteIds.Should().BeEquivalentTo([clienteRealId]);
        resultado.EmpresaIds.Should().BeEquivalentTo([empresaPropiaId]);
    }

    [Fact]
    public async Task ObtenerSubcontratasDeClienteQuery_no_incluye_la_Empresa_propia_que_tambien_sirve_al_mismo_Cliente()
    {
        Guid clienteId, subcontrataId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Con Ambos Proveedores S.A.", "B10380210", false, null, null);
            var subcontrata = Empresa.CrearComoSubcontrata("La Unica Subcontrata S.L.", "B10380228", NivelServicioSubcontrata.Supervisada.ToString());
            var empresaPropia = new Empresa("Empresa Propia Que Tambien Sirve S.L.", "B10380236");
            contexto.Empresas.AddRange(cliente, subcontrata, empresaPropia);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id; subcontrataId = subcontrata.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            // Shape Subcontrata→Cliente (la que SÍ debe aparecer) y Empresa→Cliente
            // (una Empresa propia sirviendo al mismo Cliente — NO debe colarse).
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteId, ahora, ahora),
                RelacionEmpresarial.Migrar(empresaPropiaId, clienteId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontratasDeClienteQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSubcontratasDeClienteQuery(clienteId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(subcontrataId);
    }

    [Fact]
    public async Task ObtenerSubcontrataPorIdQuery_ignora_una_relacion_ya_cerrada()
    {
        Guid subcontrataId, clienteId;
        await using (var contexto = CrearContexto())
        {
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata Con Baja S.L.", "B10380244", NivelServicioSubcontrata.Gestionada.ToString());
            var cliente = Empresa.CrearComoCliente("Cliente Que Ya No Es Servido S.A.", "B10380251", false, null, null);
            contexto.Empresas.AddRange(subcontrata, cliente);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id; clienteId = cliente.Id;

            var ahora = DateTime.UtcNow;
            var relacion = RelacionEmpresarial.Migrar(subcontrataId, clienteId, ahora.AddMonths(-6), ahora);
            relacion.Cerrar(ahora);
            contexto.RelacionesEmpresariales.Add(relacion);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontrataPorIdQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerSubcontrataPorIdQuery(subcontrataId), CancellationToken.None);

        resultado.Should().NotBeNull();
        resultado!.ClienteIds.Should().BeEmpty();
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

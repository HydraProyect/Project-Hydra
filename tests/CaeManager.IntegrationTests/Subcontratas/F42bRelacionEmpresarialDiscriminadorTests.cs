using CaeManager.Application.Clientes.Queries.ObtenerEmpresasDeCliente;
using CaeManager.Application.Clientes.Queries.ObtenerSubcontratasDeCliente;
using CaeManager.Application.Empresas.Queries.ObtenerClientesDeEmpresa;
using CaeManager.Application.Empresas.Queries.ObtenerEmpresasParaSelector;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontrataPorId;
using CaeManager.Application.Subcontratas.Queries.ObtenerSubcontratasParaSelector;
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

    /// <summary>
    /// El cambio de comportamiento real de este lector no es el discriminador
    /// (la consulta base ya filtra <c>NivelServicio != null</c>) sino el filtro
    /// de vigencia: la tabla legacy <c>SubcontratasEmpresas</c> borraba
    /// físicamente al desvincular, así que "sin fila" y "relación cerrada"
    /// eran indistinguibles. Con la arista unificada, una subcontrata que dejó
    /// de prestar servicio sigue teniendo su fila — sin el filtro, el selector
    /// seguiría ofreciéndola.
    /// </summary>
    [Fact]
    public async Task ObtenerSubcontratasParaSelectorQuery_no_ofrece_una_subcontrata_cuya_relacion_ya_esta_cerrada()
    {
        Guid empresaPropiaId, subcontrataVigenteId;
        await using (var contexto = CrearContexto())
        {
            var empresaPropia = new Empresa("Empresa Propia Del Selector S.L.", "B10380269");
            var vigente = Empresa.CrearComoSubcontrata("Subcontrata Todavia Activa S.L.", "B10380277", NivelServicioSubcontrata.Gestionada.ToString());
            var cerrada = Empresa.CrearComoSubcontrata("Subcontrata Ya Desvinculada S.L.", "B10380285", NivelServicioSubcontrata.Gestionada.ToString());
            var sinRelacion = Empresa.CrearComoSubcontrata("Subcontrata Sin Vinculo S.L.", "B10380293", NivelServicioSubcontrata.Supervisada.ToString());
            contexto.Empresas.AddRange(empresaPropia, vigente, cerrada, sinRelacion);
            await contexto.SaveChangesAsync();
            empresaPropiaId = empresaPropia.Id; subcontrataVigenteId = vigente.Id;

            var ahora = DateTime.UtcNow;
            var relacionCerrada = RelacionEmpresarial.Migrar(cerrada.Id, empresaPropiaId, ahora.AddMonths(-3), ahora);
            relacionCerrada.Cerrar(ahora);
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataVigenteId, empresaPropiaId, ahora, ahora),
                relacionCerrada);
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerSubcontratasParaSelectorQueryHandler(lectura);
        var resultado = await handler.Handle(new ObtenerSubcontratasParaSelectorQuery(empresaPropiaId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(subcontrataVigenteId);
    }

    /// <summary>
    /// Espejo de <c>ObtenerSubcontratasDeCliente</c>: el mismo Cliente servido
    /// a la vez por una Empresa propia y por una Subcontrata — situación
    /// corriente, no caso límite. La pestaña "Empresas" solo debe mostrar la
    /// Empresa propia.
    /// </summary>
    [Fact]
    public async Task ObtenerEmpresasDeClienteQuery_no_incluye_la_Subcontrata_que_tambien_sirve_al_mismo_Cliente()
    {
        Guid clienteId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Con Los Dos Proveedores S.A.", "B10380301", false, null, null);
            var empresaPropia = new Empresa("La Empresa Propia Correcta S.L.", "B10380319");
            var subcontrata = Empresa.CrearComoSubcontrata("La Subcontrata Que No Toca S.L.", "B10380327", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(cliente, empresaPropia, subcontrata);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(empresaPropiaId, clienteId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrata.Id, clienteId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerEmpresasDeClienteQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerEmpresasDeClienteQuery(clienteId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(empresaPropiaId);
    }

    /// <summary>
    /// Dirección contraria: el lado fijado es la proveedora y el ambiguo la
    /// contraparte. Una Subcontrata que presta servicio tanto a un Cliente
    /// real como a una Empresa propia solo debe listar el Cliente.
    /// </summary>
    [Fact]
    public async Task ObtenerClientesDeEmpresaQuery_no_incluye_la_Empresa_propia_a_la_que_esa_Subcontrata_sirve()
    {
        Guid subcontrataId, clienteRealId;
        await using (var contexto = CrearContexto())
        {
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata De Doble Cara S.L.", "B10380335", NivelServicioSubcontrata.Gestionada.ToString());
            var clienteReal = Empresa.CrearComoCliente("El Cliente Real Que Toca S.A.", "B10380343", false, null, null);
            var empresaPropia = new Empresa("Empresa Propia Que No Es Cliente S.L.", "B10380350");
            contexto.Empresas.AddRange(subcontrata, clienteReal, empresaPropia);
            await contexto.SaveChangesAsync();
            subcontrataId = subcontrata.Id; clienteRealId = clienteReal.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(subcontrataId, clienteRealId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrataId, empresaPropia.Id, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerClientesDeEmpresaQueryHandler(lectura, new AlcanceDatosServiceFalso());
        var resultado = await handler.Handle(new ObtenerClientesDeEmpresaQuery(subcontrataId), CancellationToken.None);

        resultado.Should().ContainSingle().Which.Id.Should().Be(clienteRealId);
    }

    /// <summary>
    /// Dos propiedades a la vez, porque el defecto tenía dos capas: sin
    /// <c>ClienteId</c> el selector ofrecía TODA la tabla <c>Empresas</c>
    /// (incluidas contrapartes, defecto heredado de F3a), y con
    /// <c>ClienteId</c> podía colar una Subcontrata que sirviera al mismo
    /// Cliente.
    /// </summary>
    [Fact]
    public async Task ObtenerEmpresasParaSelectorQuery_solo_ofrece_Empresas_propias_con_y_sin_ClienteId()
    {
        Guid clienteId, empresaPropiaId;
        await using (var contexto = CrearContexto())
        {
            var cliente = Empresa.CrearComoCliente("Cliente Del Selector De Empresas S.A.", "B10380368", false, null, null);
            var empresaPropia = new Empresa("Empresa Propia Ofrecible S.L.", "B10380376");
            var subcontrata = Empresa.CrearComoSubcontrata("Subcontrata No Ofrecible S.L.", "B10380384", NivelServicioSubcontrata.Gestionada.ToString());
            contexto.Empresas.AddRange(cliente, empresaPropia, subcontrata);
            await contexto.SaveChangesAsync();
            clienteId = cliente.Id; empresaPropiaId = empresaPropia.Id;

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.AddRange(
                RelacionEmpresarial.Migrar(empresaPropiaId, clienteId, ahora, ahora),
                RelacionEmpresarial.Migrar(subcontrata.Id, clienteId, ahora, ahora));
            await contexto.SaveChangesAsync();
        }

        await using var lectura = CrearContexto();
        var handler = new ObtenerEmpresasParaSelectorQueryHandler(lectura);

        var acotado = await handler.Handle(new ObtenerEmpresasParaSelectorQuery(clienteId), CancellationToken.None);
        acotado.Should().ContainSingle().Which.Id.Should().Be(empresaPropiaId);

        // Sin ClienteId: el catálogo global tampoco debe incluir contrapartes.
        var completo = await handler.Handle(new ObtenerEmpresasParaSelectorQuery(), CancellationToken.None);
        completo.Should().ContainSingle().Which.Id.Should().Be(empresaPropiaId);
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

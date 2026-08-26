using CaeManager.Application.Empresas.Queries.BuscarEmpresaPorCif;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.RelacionesEmpresariales;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using Xunit;

namespace CaeManager.IntegrationTests;

/// <summary>
/// F4.2a: <c>ApplicationUser.ClienteId</c> pasa a representar un
/// <c>Empresa.Id</c> (antes, un <c>Cliente.Id</c> legacy sin vía para
/// vincular ningún cliente creado después de F3b — ver doc-comment de
/// <c>ApplicationUser.ClienteId</c> y <c>f4-diseno-fisico-relacionempresarial-2026-08-26.md</c>).
///
/// Estas pruebas demuestran el comportamiento real de punta a punta que
/// ningún test cubría hasta ahora: un cliente creado HOY (después de F3b,
/// vía <c>Empresa.CrearComoCliente</c>, sin fila en la tabla legacy
/// <c>Clientes</c>) puede localizarse por CIF (<see cref="BuscarEmpresaPorCifQuery"/>)
/// y, una vez vinculado, <see cref="AlcanceDatosService"/> le da el alcance
/// correcto derivado de <c>RelacionEmpresarial</c> — con control negativo de
/// aislamiento y una comprobación de que la resolución no escala con el
/// número de relaciones (sin N+1 en el camino caliente).
/// </summary>
public class VinculacionUsuarioClienteRelacionEmpresarialTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_cadenaConexion, _tenant);
        await contexto.Database.MigrateAsync();
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task BuscarEmpresaPorCifQuery_encuentra_un_cliente_creado_despues_de_F3b()
    {
        // "Después de F3b" = vía Empresa.CrearComoCliente, sin fila en Clientes
        // — exactamente el caso que BuscarClientePorCifQuery (retirada) nunca
        // podía encontrar.
        await using var contexto = CrearContexto(_cadenaConexion, _tenant);
        var cliente = Empresa.CrearComoCliente("Iberojet Post-F3b S.A.", "B12345674", false, null, null);
        contexto.Empresas.Add(cliente);
        await contexto.SaveChangesAsync();

        var handler = new BuscarEmpresaPorCifQueryHandler(contexto);
        var encontrado = await handler.Handle(new BuscarEmpresaPorCifQuery("b12345674"), CancellationToken.None);

        encontrado.Should().NotBeNull();
        encontrado!.Id.Should().Be(cliente.Id);
        encontrado.RazonSocial.Should().Be("Iberojet Post-F3b S.A.");
    }

    [Fact]
    public async Task BuscarEmpresaPorCifQuery_no_encuentra_un_CIF_inexistente()
    {
        await using var contexto = CrearContexto(_cadenaConexion, _tenant);
        var handler = new BuscarEmpresaPorCifQueryHandler(contexto);

        var encontrado = await handler.Handle(new BuscarEmpresaPorCifQuery("B99999999"), CancellationToken.None);

        encontrado.Should().BeNull();
    }

    [Fact]
    public async Task Usuario_Cliente_vinculado_por_EmpresaId_ve_exactamente_las_Empresas_y_Subcontratas_que_lo_sirven()
    {
        var usuarioId = Guid.NewGuid();
        Guid clienteId, empresaPropiaId, subcontrataId, otroClienteId, empresaPropiaAjenaId;

        await using (var contexto = CrearContexto(_cadenaConexion, _tenant))
        {
            // El cliente que el usuario de portal representa — creado como
            // cualquier cliente hoy, sin fila en la tabla legacy Clientes.
            var cliente = Empresa.CrearComoCliente("Iberojet S.A.", "B12345674", false, null, null);
            var empresaPropia = new Empresa("Refrielectric S.L.");
            var subcontrata = Empresa.CrearComoSubcontrata("Medicion de Temperatura S.L.", null, "Gestionada");

            // Un segundo cliente del mismo tenant, servido por una Empresa
            // propia DISTINTA — control negativo de que el alcance no se
            // desborda al resto de la cartera del tenant.
            var otroCliente = Empresa.CrearComoCliente("Otro Cliente S.A.", "B87654323", false, null, null);
            var empresaPropiaAjena = new Empresa("Otra Contrata S.L.");

            contexto.Empresas.AddRange(cliente, empresaPropia, subcontrata, otroCliente, empresaPropiaAjena);
            await contexto.SaveChangesAsync();

            var ahora = DateTime.UtcNow;
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaPropia.Id, cliente.Id, ahora));
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(subcontrata.Id, cliente.Id, ahora));
            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaPropiaAjena.Id, otroCliente.Id, ahora));
            await contexto.SaveChangesAsync();

            // El vínculo que F4.2a corrige: ApplicationUser.ClienteId = Empresa.Id
            // (nunca un Id de la tabla legacy Clientes, que ni siquiera tiene
            // fila para este cliente).
            contexto.Users.Add(new ApplicationUser
            {
                Id = usuarioId,
                TenantId = _tenant,
                ClienteId = cliente.Id,
                UserName = "portal@iberojet",
                Email = "portal@iberojet"
            });
            await contexto.SaveChangesAsync();

            clienteId = cliente.Id;
            empresaPropiaId = empresaPropia.Id;
            subcontrataId = subcontrata.Id;
            otroClienteId = otroCliente.Id;
            empresaPropiaAjenaId = empresaPropiaAjena.Id;
        }

        await using var contextoLectura = CrearContexto(_cadenaConexion, _tenant);
        var servicio = new AlcanceDatosService(
            contextoLectura,
            new CurrentUserServiceFalso(usuarioId, "Cliente"),
            new TenantActualAmbiental { TenantId = _tenant },
            new SesionPrivilegiadaAusente());

        var clienteIdsVisibles = await servicio.ObtenerClienteIdsVisiblesAsync();
        var empresaIdsVisibles = await servicio.ObtenerEmpresaIdsVisiblesAsync();
        var subcontrataIdsVisibles = await servicio.ObtenerSubcontrataIdsVisiblesAsync();

        clienteIdsVisibles.Should().ContainSingle().Which.Should().Be(clienteId);

        empresaIdsVisibles.Should().NotBeNull();
        empresaIdsVisibles!.Should().Contain(empresaPropiaId)
            .And.NotContain(empresaPropiaAjenaId, "sirve a otro cliente del mismo tenant, no al que representa este usuario");

        subcontrataIdsVisibles.Should().NotBeNull();
        subcontrataIdsVisibles!.Should().ContainSingle().Which.Should().Be(subcontrataId);

        _ = otroClienteId; // usado solo para construir el dato ajeno de arriba
    }

    [Fact]
    public async Task Usuario_Cliente_no_ve_datos_de_una_Empresa_o_tenant_ajeno()
    {
        var usuarioId = Guid.NewGuid();
        var tenantAjeno = Guid.NewGuid();
        Guid clienteId;

        await using (var contexto = CrearContexto(_cadenaConexion, _tenant))
        {
            var cliente = Empresa.CrearComoCliente("Iberojet S.A.", "B12345674", false, null, null);
            var empresaPropia = new Empresa("Refrielectric S.L.");
            contexto.Empresas.AddRange(cliente, empresaPropia);
            await contexto.SaveChangesAsync();

            contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaPropia.Id, cliente.Id, DateTime.UtcNow));

            contexto.Users.Add(new ApplicationUser
            {
                Id = usuarioId,
                TenantId = _tenant,
                ClienteId = cliente.Id,
                UserName = "portal@iberojet2",
                Email = "portal@iberojet2"
            });
            await contexto.SaveChangesAsync();

            clienteId = cliente.Id;
        }

        // Dato con la MISMA forma en un tenant totalmente distinto — si el
        // aislamiento de tenant fallara, este Id filtraría en la consulta de
        // abajo por coincidir por casualidad de ClienteId.
        await using (var contextoAjeno = CrearContexto(_cadenaConexion, tenantAjeno))
        {
            var clienteAjeno = Empresa.CrearComoCliente("Cliente de otro tenant", "B12345674", false, null, null);
            contextoAjeno.Empresas.Add(clienteAjeno);
            await contextoAjeno.SaveChangesAsync();
        }

        await using var contextoLectura = CrearContexto(_cadenaConexion, _tenant);
        var servicio = new AlcanceDatosService(
            contextoLectura,
            new CurrentUserServiceFalso(usuarioId, "Cliente"),
            new TenantActualAmbiental { TenantId = _tenant },
            new SesionPrivilegiadaAusente());

        (await servicio.ObtenerClienteIdsVisiblesAsync()).Should().ContainSingle().Which.Should().Be(clienteId);
    }

    [Fact]
    public async Task La_resolucion_del_alcance_Cliente_no_escala_con_el_numero_de_relaciones_N_mas_1()
    {
        var usuarioId = Guid.NewGuid();

        await using (var contexto = CrearContexto(_cadenaConexion, _tenant))
        {
            var cliente = Empresa.CrearComoCliente("Cliente con muchas relaciones S.A.", "B12345674", false, null, null);
            contexto.Empresas.Add(cliente);
            await contexto.SaveChangesAsync();

            var ahora = DateTime.UtcNow;
            for (var i = 0; i < 25; i++)
            {
                var empresaPropia = new Empresa($"Contrata {i} S.L.");
                contexto.Empresas.Add(empresaPropia);
                await contexto.SaveChangesAsync();
                contexto.RelacionesEmpresariales.Add(RelacionEmpresarial.Crear(empresaPropia.Id, cliente.Id, ahora));
            }
            await contexto.SaveChangesAsync();

            contexto.Users.Add(new ApplicationUser
            {
                Id = usuarioId,
                TenantId = _tenant,
                ClienteId = cliente.Id,
                UserName = "portal@muchasrelaciones",
                Email = "portal@muchasrelaciones"
            });
            await contexto.SaveChangesAsync();
        }

        var contador = new ContadorComandosInterceptor();
        var tenantActual = new TenantActualAmbiental { TenantId = _tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(contador)
            .Options;

        await using var contextoContado = new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
        var servicio = new AlcanceDatosService(
            contextoContado, new CurrentUserServiceFalso(usuarioId, "Cliente"), tenantActual, new SesionPrivilegiadaAusente());

        var empresasVisibles = await servicio.ObtenerEmpresaIdsVisiblesAsync();

        empresasVisibles.Should().HaveCount(25);
        // Una consulta para ClienteId (Users), una para el propio alcance de
        // Empresa (RelacionesEmpresariales+Empresas) — constante, no 25+1:
        // si alguien introdujera un bucle por relación, este número crecería
        // con el tamaño de la cartera del cliente.
        contador.NumeroDeComandos.Should().BeLessOrEqualTo(3,
            "resolver el alcance de un Cliente no debe emitir una consulta por relación (N+1)");
    }

    private sealed class ContadorComandosInterceptor : DbCommandInterceptor
    {
        public int NumeroDeComandos { get; private set; }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            NumeroDeComandos++;
            return base.ReaderExecuting(command, eventData, result);
        }
    }

    private static CaeManagerDbContext CrearContexto(string cadenaConexion, Guid tenant)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenant };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

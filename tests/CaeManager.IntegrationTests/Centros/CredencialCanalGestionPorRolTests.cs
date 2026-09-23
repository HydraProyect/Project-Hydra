using CaeManager.Application.Centros.Queries.ObtenerCredencialCanalGestion;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Centros;

/// <summary>
/// Qué roles obtienen la contraseña de un canal de Plataforma CAE, con la
/// composición real: <see cref="AutorizacionSecretosDeTenantBehavior{TRequest,TResponse}"/>
/// delante del handler, y el servicio de alcance REAL contra PostgreSQL.
///
/// <para>
/// Decisión del propietario (2026-09-23): solo los roles con escritura leen los
/// secretos del Tenant propietario. Antes de ella, el rol Consulta —también la
/// Consulta delegada del Operador CAE externo— obtenía la contraseña: su alcance
/// de lectura es total y el alcance de gestión solo excluía al rol Cliente. Cada
/// capa sola no basta: el behavior decide el rol, el handler el Centro.
/// </para>
/// </summary>
public class CredencialCanalGestionPorRolTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenant = Guid.NewGuid();
    private Guid _centroId, _canalId, _clienteId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto(_tenant);
        await contexto.Database.MigrateAsync();

        var cliente = Empresa.CrearComoCliente("Cliente Repro S.L.", "B10380392", false, null, null);
        var proveedora = new Empresa("Contratista Repro S.L.", "B87654331");
        contexto.Empresas.AddRange(cliente, proveedora);
        await contexto.SaveChangesAsync();

        var centro = new Centro(cliente.Id, proveedora.Id, "Centro repro");
        contexto.Centros.Add(centro);
        await contexto.SaveChangesAsync();

        var proveedor = await contexto.ProveedoresPlataformaCae.FirstAsync();
        var canal = CanalGestionDocumental.DePlataforma(
            centro.Id, "Acceso", proveedor.Id, null, "usuario-repro", "secreto-repro");
        contexto.CanalesGestionDocumental.Add(canal);
        await contexto.SaveChangesAsync();

        _clienteId = cliente.Id;
        _centroId = centro.Id;
        _canalId = canal.Id;
    }

    public async Task DisposeAsync() => await BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    [Fact]
    public async Task Rol_Cliente_no_obtiene_la_credencial()
    {
        var usuario = Guid.NewGuid();
        await using (var contexto = CrearContexto(_tenant))
        {
            contexto.Users.Add(new CaeManager.Infrastructure.Identity.ApplicationUser
            {
                Id = usuario, UserName = $"p-{usuario:N}@ejemplo.test", Email = $"p-{usuario:N}@ejemplo.test",
                ClienteId = _clienteId, TenantId = _tenant
            });
            await contexto.SaveChangesAsync();
        }

        (await ObtenerAsync(usuario, "Cliente", _tenant)).Should().BeNull();
    }

    [Fact]
    public async Task Control_positivo_el_Gestor_CAE_con_cartera_obtiene_la_contrasena()
    {
        // Sin él, los null de abajo podrían deberse a una siembra que no
        // encuentra el canal y no a la regla de roles. El Gestor CAE es un rol
        // de cartera: sin Asignación de Cartera sobre el Cliente empresarial
        // del Centro, su alcance de gestión es vacío y también recibiría null.
        var gestor = await OtorgarCarteraAsync(_clienteId);
        var resultado = await ObtenerAsync(gestor, "GestorCae", _tenant);
        resultado.Should().NotBeNull();
        resultado!.Contrasena.Should().Be("secreto-repro");
    }

    [Fact]
    public async Task Rol_Consulta_del_propio_Tenant_no_obtiene_la_contrasena()
    {
        (await ObtenerAsync(Guid.NewGuid(), "Consulta", _tenant)).Should().BeNull();
    }

    [Fact]
    public async Task Rol_Consulta_con_Tenant_de_origen_distinto_no_obtiene_la_contrasena()
    {
        // Consulta delegada: el rol lo resuelve CurrentUserService desde la
        // AsignacionOperadorDelegado; aquí se inyecta ya resuelto, con el
        // Tenant de origen del Operador CAE externo, distinto del propietario.
        (await ObtenerAsync(Guid.NewGuid(), "Consulta", Guid.NewGuid())).Should().BeNull();
    }

    private async Task<Guid> OtorgarCarteraAsync(Guid clienteId)
    {
        await using var contexto = CrearContexto(_tenant);
        var usuarioId = Guid.NewGuid();
        var ahora = DateTime.UtcNow;

        var raiz = AsignacionOperacion.Raiz(_tenant, ServicioCae.Outbound, ahora, ahora);
        contexto.AsignacionesOperacion.Add(raiz);
        contexto.AsignacionesCartera.Add(AsignacionCartera.Interna(
            raiz, usuarioId, AmbitoAsignacion.DeRelacionCliente(clienteId), ahora, null, ahora));

        await contexto.SaveChangesAsync();
        return usuarioId;
    }

    private async Task<CredencialCanalGestionDto?> ObtenerAsync(Guid usuario, string rol, Guid tenantOrigen)
    {
        await using var contexto = CrearContexto(_tenant);
        var usuarioActual = new CurrentUserServiceFalso(usuario, rol, tenantOrigenId: tenantOrigen);
        var sinSesion = new SesionPrivilegiadaAusente();
        var alcance = new AlcanceDatosService(
            contexto, usuarioActual, new TenantActualAmbiental { TenantId = _tenant }, sinSesion);
        var handler = new ObtenerCredencialCanalGestionQueryHandler(contexto, alcance);
        var behavior = new AutorizacionSecretosDeTenantBehavior<ObtenerCredencialCanalGestionQuery, CredencialCanalGestionDto?>(
            sinSesion, usuarioActual);
        var consulta = new ObtenerCredencialCanalGestionQuery(_centroId, _canalId);
        return await behavior.Handle(consulta, ct => handler.Handle(consulta, ct), CancellationToken.None);
    }

    private CaeManagerDbContext CrearContexto(Guid tenantId)
    {
        var tenantActual = new TenantActualAmbiental { TenantId = tenantId };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

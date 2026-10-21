using CaeManager.Application.Common;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Operaciones;
using CaeManager.Domain.Tenants;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Usuarios;

/// <summary>
/// <see cref="DirectorioUsuariosTenant.ObtenerEmpresasGestionadasAsync"/> alimenta la otra mitad de la
/// columna «Ámbito efectivo» de <c>/usuarios</c>: las Empresas gestionadas (Tenants beneficiarios) de cada
/// persona del Operador CAE. Bug del 21-10 con la siembra del piloto Outbound: mirada desde el Operador CAE
/// externo, una Gestora CAE con cinco Empresas gestionadas salía «Sin cartera».
///
/// <para>
/// Con el cableado de producción (<see cref="ArnesDeArranqueRuntime"/>: <c>cae_app_runtime</c> y sus
/// interceptores), porque la lectura cruza Tenants y lo que interesa es que la RLS de los dos catálogos
/// de asignación la permita solo en la posición de operador, no que el filtro en C# lo parezca.
/// </para>
///
/// <para>
/// Escenario: el Operador CAE A opera enteras las Empresas X e Y, y la Empresa W acotada a un Cliente
/// empresarial. Su Gestora CAE tiene cartera vigente en X y W, y una cerrada en Y. Una persona del Tenant C
/// tiene una cartera mal formada bajo la operación de A sobre X. El Operador CAE B opera a la Empresa Z y su
/// Gestor CAE tiene cartera en ella. La Empresa V la opera A acotada en Outbound y entera en Inbound, con
/// cartera de la Gestora en las dos.
/// </para>
/// </summary>
public class EmpresasGestionadasDelAmbitoEfectivoBajoRlsTests : IAsyncLifetime
{
    private readonly UsuarioConmutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private readonly Guid _gestoraDeA = Guid.NewGuid();
    private readonly Guid _gestorDeB = Guid.NewGuid();
    private readonly Guid _personaDeC = Guid.NewGuid();
    private Guid _operadorA, _operadorB, _tenantC, _empresaX, _empresaY, _empresaZ, _empresaW, _empresaV;

    public async Task InitializeAsync()
    {
        _arnes = await ArnesDeArranqueRuntime.CrearAsync(datosDePruebaActivos: false, currentUserServicePersonalizado: _sesion);

        // La siembra va como propietario de las tablas: las asignaciones las crea el alta, no quien mira.
        var tenantActual = new TenantActualPorAmbito();
        var opciones = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_arnes.CadenaPropietario, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;
        await using var propietario = new CaeManagerDbContext(opciones, new EphemeralDataProtectionProvider(), tenantActual);

        var a = new Tenant("Operador A");
        var b = new Tenant("Operador B");
        var c = new Tenant("Tenant C");
        var x = new Tenant("Empresa X");
        var y = new Tenant("Empresa Y");
        var z = new Tenant("Empresa Z");
        var w = new Tenant("Empresa W");
        var v = new Tenant("Empresa V");
        propietario.Tenants.AddRange(a, b, c, x, y, z, w, v);
        await propietario.SaveChangesAsync();
        (_operadorA, _operadorB, _tenantC) = (a.Id, b.Id, c.Id);
        (_empresaX, _empresaY, _empresaZ, _empresaW, _empresaV) = (x.Id, y.Id, z.Id, w.Id, v.Id);

        propietario.Users.AddRange(
            CrearUsuario(_gestoraDeA, _operadorA, "gestora.a"),
            CrearUsuario(_gestorDeB, _operadorB, "gestor.b"),
            CrearUsuario(_personaDeC, _tenantC, "persona.c"));
        await propietario.SaveChangesAsync();

        Guid clienteDeW, clienteDeV;
        using (AmbitoTenantExplicito.Establecer(_empresaW))
        {
            var cliente = Empresa.CrearComoCliente("Cliente de W S.A.", "B12345674", false, null, null);
            propietario.Empresas.Add(cliente);
            await propietario.SaveChangesAsync();
            clienteDeW = cliente.Id;
        }
        using (AmbitoTenantExplicito.Establecer(_empresaV))
        {
            var cliente = Empresa.CrearComoCliente("Cliente de V S.A.", "B12345674", false, null, null);
            propietario.Empresas.Add(cliente);
            await propietario.SaveChangesAsync();
            clienteDeV = cliente.Id;
        }

        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-2);
        var sobreX = AsignacionOperacion.Externa(_empresaX, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        var sobreY = AsignacionOperacion.Externa(_empresaY, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        var sobreW = AsignacionOperacion.Externa(
            _empresaW, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDeW), desde, null, ahora);
        var sobreZ = AsignacionOperacion.Externa(_empresaZ, _operadorB, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora);
        // V: la misma Empresa, acotada a un Cliente empresarial en Outbound y entera en Inbound.
        var sobreVAcotada = AsignacionOperacion.Externa(
            _empresaV, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDeV), desde, null, ahora);
        var sobreVEntera = AsignacionOperacion.Externa(_empresaV, _operadorA, ServicioCae.Inbound, AmbitoAsignacion.Universal, desde, null, ahora);
        using (AmbitoTenantExplicito.Establecer(_operadorA))
            propietario.AsignacionesOperacion.AddRange(sobreX, sobreY, sobreW, sobreZ, sobreVAcotada, sobreVEntera);
        await propietario.SaveChangesAsync();

        var cerradaEnY = AsignacionCartera.Externa(sobreY, _gestoraDeA, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora);
        cerradaEnY.Cerrar(MotivoCierreAsignacion.Revocada, ahora);
        propietario.AsignacionesCartera.AddRange(
            AsignacionCartera.Externa(sobreX, _gestoraDeA, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora),
            AsignacionCartera.Externa(sobreW, _gestoraDeA, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora),
            AsignacionCartera.Externa(sobreVAcotada, _gestoraDeA, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora),
            AsignacionCartera.Externa(sobreVEntera, _gestoraDeA, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora),
            cerradaEnY,
            AsignacionCartera.Externa(sobreX, _personaDeC, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora),
            AsignacionCartera.Externa(sobreZ, _gestorDeB, "GestorCae", AmbitoAsignacion.Universal, desde, null, ahora));
        await propietario.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task La_Direccion_CAE_del_Operador_ve_las_Empresas_gestionadas_de_su_Gestora_y_nada_del_otro_Operador()
    {
        var deA = await LeerAsync(sesion: Guid.NewGuid(), tenantOrigen: _operadorA, tenantActivo: _operadorA);

        deA.PorCartera.Keys.Should().BeEquivalentTo([_gestoraDeA],
            "el Gestor CAE del Operador B no es de esta organización, y la persona del Tenant C tampoco aunque su " +
            "cartera cuelgue de una operación de A");
        deA.PorCartera[_gestoraDeA].Should().BeEquivalentTo(
            [new EmpresaGestionada(_empresaX, "Empresa X", Parcial: false), new EmpresaGestionada(_empresaW, "Empresa W", Parcial: true),
             new EmpresaGestionada(_empresaV, "Empresa V", Parcial: false)],
            "la cartera cerrada en Y no concede nada, la de W cuelga de una operación acotada a un Cliente empresarial, " +
            "y V cuenta una vez y entera porque una de sus dos operaciones lo es");
        deA.DelTenantEntero.Select(e => e.TenantBeneficiarioId).Should().BeEquivalentTo([_empresaX, _empresaY, _empresaV],
            "un Coordinador CAE entra sin cartera solo en las Empresas gestionadas que su organización opera enteras");

        var deB = await LeerAsync(sesion: Guid.NewGuid(), tenantOrigen: _operadorB, tenantActivo: _operadorB);
        deB.PorCartera.Keys.Should().BeEquivalentTo([_gestorDeB]);
        deB.PorCartera[_gestorDeB].Select(e => e.TenantBeneficiarioId).Should().Equal([_empresaZ]);
        deB.DelTenantEntero.Select(e => e.TenantBeneficiarioId).Should().Equal([_empresaZ]);
    }

    [Fact]
    public async Task Dentro_de_una_Empresa_gestionada_no_revela_que_otras_Empresas_opera_el_Operador()
    {
        // La propia Gestora, con su cuenta visible para la RLS dentro de X: sin la guarda de «Tenant activo ==
        // origen», la posición de operador le devolvería su cartera también aquí.
        (await LeerAsync(sesion: _gestoraDeA, tenantOrigen: _operadorA, tenantActivo: _empresaX)).Should().BeEquivalentTo(
            EmpresasGestionadasDeLaOrganizacion.Ninguna,
            "con el Tenant activo distinto del de origen, las personas listadas no son de la organización que opera");

        (await LeerAsync(sesion: Guid.NewGuid(), tenantOrigen: _empresaX, tenantActivo: _empresaX)).Should().BeEquivalentTo(
            EmpresasGestionadasDeLaOrganizacion.Ninguna,
            "la Empresa gestionada no ocupa la posición de operador: no lee la cartera de su Operador CAE");
    }

    private async Task<EmpresasGestionadasDeLaOrganizacion> LeerAsync(Guid sesion, Guid tenantOrigen, Guid tenantActivo)
    {
        _sesion.UsuarioId = sesion;
        _sesion.Rol = "DireccionCae";
        _sesion.TenantOrigenId = tenantOrigen;

        // El arnés no registra el directorio (vive en el cableado de Web): se monta con el contexto de producción
        // del ámbito, que es el que conecta como cae_app_runtime. userManager y el contexto de Tenants van a
        // null a propósito: este método no los toca, y si un día lo hiciera el test estalla en vez de pasar.
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var directorio = new DirectorioUsuariosTenant(
            null!, null!, new TenantActualPorAmbito(), new PuertaAccesoDatos(), contexto, _sesion);
        using (AmbitoTenantExplicito.Establecer(tenantActivo))
            return await directorio.ObtenerEmpresasGestionadasAsync();
    }

    private static ApplicationUser CrearUsuario(Guid id, Guid tenantId, string alias) => new()
    {
        Id = id,
        TenantId = tenantId,
        UserName = $"{alias}@caemanager.local",
        NormalizedUserName = $"{alias}@CAEMANAGER.LOCAL".ToUpperInvariant(),
        Email = $"{alias}@caemanager.local",
        NormalizedEmail = $"{alias}@CAEMANAGER.LOCAL".ToUpperInvariant(),
        NombreCompleto = alias,
        EmailConfirmed = true,
        SecurityStamp = Guid.NewGuid().ToString(),
        ConcurrencyStamp = Guid.NewGuid().ToString()
    };

    private sealed class TenantActualPorAmbito : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual;
    }

    /// <summary>La persona de la sesión, cambiable entre pasos: el interceptor la lee en cada apertura de conexión.</summary>
    private sealed class UsuarioConmutable : ICurrentUserService
    {
        public Guid? UsuarioId { get; set; }
        public string? Rol { get; set; }
        public Guid? TenantOrigenId { get; set; }

        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult(UsuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(Rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult(Rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult(TenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}

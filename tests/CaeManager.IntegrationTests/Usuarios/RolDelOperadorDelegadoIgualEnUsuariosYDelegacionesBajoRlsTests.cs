using CaeManager.Application.Common;
using CaeManager.Application.Tenants;
using CaeManager.Application.Tenants.Queries.ObtenerDelegaciones;
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
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CaeManager.IntegrationTests.Usuarios;

/// <summary>
/// La misma persona del Operador CAE externo, en el mismo Tenant beneficiario, tiene el mismo rol en
/// la columna Rol de <c>/usuarios</c> (<see cref="DirectorioUsuariosTenant.ObtenerRolesDeOperadoresDelegadosAsync"/>)
/// y en la lista de personas de <c>/delegaciones</c> (<see cref="ObtenerDelegacionesQuery"/> más
/// <see cref="TenantsBeneficiariosAutorizados.RolEjercidoEnTenantBeneficiario"/>, como lo combina
/// <c>Delegaciones.razor.cs</c>). Antes, un Administrador del Operador CAE externo salía «Gestor CAE» en
/// <c>/usuarios</c>, que pintaba el rol guardado en la cartera, y «Administrador» en <c>/delegaciones</c>
/// (deuda declarada en la PR #1550; decisión Chris 2026-10-20: el rol en las Empresas gestionadas es el
/// perfil de origen, con operación vigente del Tenant entero).
///
/// <para>
/// Con el cableado de producción (<see cref="ArnesDeArranqueRuntime"/>: <c>cae_app_runtime</c> y sus
/// interceptores), porque las dos lecturas cruzan Tenants y la coherencia que importa es la que deja la
/// RLS de <c>AsignacionesOperacion</c>, no la que parece en C#.
/// </para>
///
/// <para>
/// Escenario: el Operador CAE A opera la Empresa X entera, la Empresa Y acotada a un Cliente empresarial y
/// la Empresa Z con una operación ya caducada. Su Administradora (perfil de origen Administrador) está
/// delegada en las tres con el rol guardado «GestorCae»; su Gestora CAE, en X. La Plataforma P tiene una
/// ventana de Soporte TALVEG abierta sobre X, con rol guardado «Consulta», para una persona cuyo perfil de
/// origen es Administrador, y además una operación del Tenant entero sobre X: el Soporte TALVEG nunca
/// sube al perfil de origen, ni siquiera entonces.
/// </para>
/// </summary>
public class RolDelOperadorDelegadoIgualEnUsuariosYDelegacionesBajoRlsTests : IAsyncLifetime
{
    private readonly UsuarioConmutable _sesion = new();
    private ArnesDeArranqueRuntime _arnes = null!;

    private readonly Guid _administradoraDeA = Guid.NewGuid();
    private readonly Guid _gestoraDeA = Guid.NewGuid();
    private readonly Guid _soporte = Guid.NewGuid();
    private Guid _operadorA, _plataforma, _empresaX, _empresaY, _empresaZ;

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
        var p = new Tenant("Plataforma P");
        var x = new Tenant("Empresa X");
        var y = new Tenant("Empresa Y");
        var z = new Tenant("Empresa Z");
        propietario.Tenants.AddRange(a, p, x, y, z);
        await propietario.SaveChangesAsync();
        (_operadorA, _plataforma, _empresaX, _empresaY, _empresaZ) = (a.Id, p.Id, x.Id, y.Id, z.Id);

        // Los roles los siembran las migraciones.
        var roles = await propietario.Roles.AsNoTracking().ToDictionaryAsync(r => r.Name!, r => r.Id);
        void Cuenta(Guid id, Guid tenantId, string alias, string rol)
        {
            propietario.Users.Add(CrearUsuario(id, tenantId, alias));
            propietario.UserRoles.Add(new IdentityUserRole<Guid> { UserId = id, RoleId = roles[rol] });
        }

        Cuenta(_administradoraDeA, _operadorA, "administradora.a", "Administrador");
        Cuenta(_gestoraDeA, _operadorA, "gestora.a", "GestorCae");
        Cuenta(_soporte, _plataforma, "soporte.p", "Administrador");
        await propietario.SaveChangesAsync();

        Guid clienteDeY;
        using (AmbitoTenantExplicito.Establecer(_empresaY))
        {
            var cliente = Empresa.CrearComoCliente("Cliente de Y S.A.", "B12345674", false, null, null);
            propietario.Empresas.Add(cliente);
            await propietario.SaveChangesAsync();
            clienteDeY = cliente.Id;
        }

        var ahora = DateTime.UtcNow;
        var desde = ahora.AddDays(-10);
        using (AmbitoTenantExplicito.Establecer(_operadorA))
            propietario.AsignacionesOperacion.AddRange(
                AsignacionOperacion.Externa(_empresaX, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, null, ahora),
                AsignacionOperacion.Externa(
                    _empresaY, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.DeRelacionCliente(clienteDeY), desde, null, ahora),
                AsignacionOperacion.Externa(
                    _empresaZ, _operadorA, ServicioCae.Outbound, AmbitoAsignacion.Universal, desde, ahora.AddDays(-1), ahora),
                // Otro servicio: IX_AsignacionesOperacion_DelegacionTotalVigente admite una sola operación vigente del
                // Tenant entero por propietario y servicio. OperacionesDelTenantEntero no mira el servicio.
                AsignacionOperacion.Externa(_empresaX, _plataforma, ServicioCae.Inbound, AmbitoAsignacion.Universal, desde, null, ahora));
        await propietario.SaveChangesAsync();

        var sobreX = new DelegacionTenant(_operadorA, _empresaX);
        var sobreY = new DelegacionTenant(_operadorA, _empresaY);
        var sobreZ = new DelegacionTenant(_operadorA, _empresaZ);
        var soporteSobreX = DelegacionTenant.ParaSoporte(_plataforma, _empresaX);
        soporteSobreX.ActivarParaSoporte("Incidencia de prueba", ahora.AddHours(2), ahora);
        propietario.DelegacionesTenant.AddRange(sobreX, sobreY, sobreZ, soporteSobreX);
        propietario.AsignacionesOperadorDelegadoConRevocadas.AddRange(
            new AsignacionOperadorDelegado(sobreX.Id, _administradoraDeA, "GestorCae"),
            new AsignacionOperadorDelegado(sobreX.Id, _gestoraDeA, "GestorCae"),
            new AsignacionOperadorDelegado(sobreY.Id, _administradoraDeA, "GestorCae"),
            new AsignacionOperadorDelegado(sobreZ.Id, _administradoraDeA, "GestorCae"),
            new AsignacionOperadorDelegado(soporteSobreX.Id, _soporte, "Consulta"));
        await propietario.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _arnes.DisposeAsync();

    [Fact]
    public async Task Con_operacion_del_Tenant_entero_las_dos_pantallas_dicen_el_perfil_de_origen_y_Soporte_su_rol_guardado()
    {
        // Mirado por el Administrador de la propia Empresa X, que ve en /delegaciones todas las que la afectan.
        var (usuarios, delegaciones) = await LeerAsync(sesion: Guid.NewGuid(), tenantOrigen: _empresaX, tenantActivo: _empresaX);

        delegaciones.Should().BeEquivalentTo(new Dictionary<Guid, string>
        {
            [_administradoraDeA] = "Administrador",
            [_gestoraDeA] = "GestorCae",
            [_soporte] = "Consulta",
        }, "con operación vigente del Tenant entero rige el perfil de origen; Soporte TALVEG conserva el de su ventana");
        usuarios.Should().BeEquivalentTo(delegaciones,
            "la misma persona en el mismo Tenant beneficiario no puede ser «Gestor CAE» en /usuarios y «Administrador» en /delegaciones");
    }

    [Fact]
    public async Task Dentro_de_la_Empresa_gestionada_la_Gestora_del_Operador_ve_el_mismo_rol_en_las_dos_pantallas()
    {
        // La Gestora CAE de A, en el contexto delegado de X: /delegaciones solo trae las de su organización.
        var (usuarios, delegaciones) = await LeerAsync(sesion: _gestoraDeA, tenantOrigen: _operadorA, tenantActivo: _empresaX);

        delegaciones.Should().BeEquivalentTo(new Dictionary<Guid, string>
        {
            [_administradoraDeA] = "Administrador",
            [_gestoraDeA] = "GestorCae",
        });
        usuarios.Where(r => delegaciones.ContainsKey(r.Key)).Should().BeEquivalentTo(delegaciones);
        usuarios[_soporte].Should().Be("Consulta",
            "la RLS deja leer la operación de P sobre X por la posición de propietario, y aun así Soporte no sube");
    }

    [Fact]
    public async Task Con_operacion_acotada_o_caducada_las_dos_pantallas_dicen_el_rol_guardado()
    {
        foreach (var empresa in new[] { _empresaY, _empresaZ })
        {
            var (usuarios, delegaciones) = await LeerAsync(sesion: Guid.NewGuid(), tenantOrigen: empresa, tenantActivo: empresa);

            delegaciones.Should().BeEquivalentTo(new Dictionary<Guid, string> { [_administradoraDeA] = "GestorCae" },
                "sin operación vigente del Tenant entero el perfil de origen no rige y se enseña el rol guardado");
            usuarios.Should().BeEquivalentTo(delegaciones);
        }

        // Desde el Operador CAE A en el contexto delegado de Y: la RLS le deja leer también su operación del Tenant
        // entero sobre X por la posición de operador, y esa no puede subir a nadie en Y.
        var (usuariosDesdeA, delegacionesDesdeA) = await LeerAsync(
            sesion: _administradoraDeA, tenantOrigen: _operadorA, tenantActivo: _empresaY);
        delegacionesDesdeA.Should().BeEquivalentTo(new Dictionary<Guid, string> { [_administradoraDeA] = "GestorCae" },
            "la operación entera de A sobre X no cuenta en Y, donde A solo opera un Cliente empresarial");
        usuariosDesdeA.Should().BeEquivalentTo(delegacionesDesdeA);
    }

    /// <summary>
    /// Las dos lecturas con la sesión dada. La de <c>/delegaciones</c> combina la consulta con el perfil de
    /// origen de cada persona igual que <c>Delegaciones.razor.cs</c> (<c>CargarNombresDeOperadoresAsync</c>):
    /// un rol de Identity por cuenta, el menor en orden ordinal si hubiera dos. Solo las delegaciones activas
    /// del Tenant activo, que son las que <c>/usuarios</c> lista.
    /// </summary>
    private async Task<(IReadOnlyDictionary<Guid, string> Usuarios, Dictionary<Guid, string> Delegaciones)> LeerAsync(
        Guid sesion, Guid tenantOrigen, Guid tenantActivo)
    {
        _sesion.UsuarioId = sesion;
        _sesion.Rol = "Administrador";
        _sesion.TenantOrigenId = tenantOrigen;

        // El arnés no registra el directorio (vive en el cableado de Web): se monta con el contexto de producción
        // del ámbito, que es el que conecta como cae_app_runtime. userManager va a null a propósito: este método
        // no lo toca, y si un día lo hiciera el test estalla en vez de pasar.
        await using var scope = _arnes.Servicios.CreateAsyncScope();
        var contexto = scope.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
        var directorio = new DirectorioUsuariosTenant(
            null!, contexto, new TenantActualPorAmbito(), new PuertaAccesoDatos(), contexto, _sesion);
        var consulta = new ObtenerDelegacionesQueryHandler(contexto, _sesion, contexto);

        using (AmbitoTenantExplicito.Establecer(tenantActivo))
        {
            var usuarios = await directorio.ObtenerRolesDeOperadoresDelegadosAsync();

            var lista = await consulta.Handle(
                new ObtenerDelegacionesQuery(tenantOrigen == tenantActivo ? null : tenantActivo), CancellationToken.None);
            var personas = lista
                .Where(d => d.Activa && d.TenantClienteId == tenantActivo)
                .SelectMany(d => d.Operadores.Select(o => (d.EsSoporte, d.OperacionDelTenantEntero, o.UsuarioId, o.Rol)))
                .ToList();
            var usuarioIds = personas.Select(p => p.UsuarioId).Distinct().ToList();
            var rolesDeOrigen = (await (
                    from usuarioRol in contexto.UserRoles
                    where usuarioIds.Contains(usuarioRol.UserId)
                    join rol in contexto.Roles on usuarioRol.RoleId equals rol.Id
                    select new { usuarioRol.UserId, rol.Name })
                    .ToListAsync())
                .GroupBy(r => r.UserId)
                .ToDictionary(g => g.Key, g => g.Select(r => r.Name!).Order(StringComparer.Ordinal).First());

            var delegaciones = personas
                .GroupBy(p => p.UsuarioId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(p => TenantsBeneficiariosAutorizados.RolEjercidoEnTenantBeneficiario(
                            p.EsSoporte, p.OperacionDelTenantEntero, p.Rol, rolesDeOrigen.GetValueOrDefault(p.UsuarioId)))
                        .Distinct()
                        .Single());

            return (usuarios, delegaciones);
        }
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

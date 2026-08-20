using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
// Alias: el espacio de nombres de este test termina en .Plataforma y tapa el de
// Infrastructure, así que el tipo se nombra sin ambigüedad.
using SesionPrivilegiadaActual = CaeManager.Infrastructure.Plataforma.SesionPrivilegiadaActual;
using CaeManager.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// Batería adversaria de la resolución de sesiones privilegiadas: el punto por
/// el que pasa <b>todo</b> el plano 3 antes de conceder nada.
///
/// La tesis que estos tests atacan es la del ADR-011 § 8.1 — los tres estados
/// no se pueden colapsar en uno:
/// <code>
/// concesión existe  ≠  concesión válida ahora  ≠  sesión activa
/// </code>
/// Cada test toma una sesión que en algún momento fue perfectamente legítima y
/// rompe <b>una sola</b> de las condiciones, dejando intactas las demás. Si la
/// implementación se apoyara en la ventana que la sesión lleva grabada —el
/// error de forma más tentador, porque está ahí mismo y no cuesta una
/// consulta— la mitad de estos tests pasarían igual y el acceso seguiría vivo
/// después de revocar la concesión. Es el mismo error que en su día dejaba
/// operando a un usuario retirado de su cartera: comprobar el contenedor y no
/// el permiso.
///
/// Se prueba contra Postgres real y contra la clase de producción, no contra
/// dobles: lo que importa no es que el método devuelva null cuando se le pide,
/// sino que la consulta que realmente sale a la base excluya estas filas.
/// </summary>
public class SesionPrivilegiadaActualTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _usuarioPlataforma = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();

    private Guid _concesionId;
    private Guid _sesionId;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _usuarioPlataforma, CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4),
            concedidaPorUsuarioId: _usuarioPlataforma, motivoConcesion: "Incidencia de prueba");

        var sesion = SesionPrivilegiada.Abrir(
            concesion, _tenantVisitado, "Reproducir la incidencia", ahora.AddMinutes(-1), TimeSpan.FromHours(1));

        contexto.ConcesionesPrivilegio.Add(concesion);
        contexto.SesionesPrivilegiadas.Add(sesion);
        await contexto.SaveChangesAsync();

        _concesionId = concesion.Id;
        _sesionId = sesion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    /// <summary>
    /// Control positivo. Sin él, todo lo demás podría estar pasando porque la
    /// consulta no encuentra nunca nada.
    /// </summary>
    [Fact]
    public async Task Una_sesion_viva_bajo_una_concesion_vigente_resuelve()
    {
        var resuelta = await ResolverAsync();

        resuelta.Should().NotBeNull();
        resuelta!.Value.SesionId.Should().Be(_sesionId);
        resuelta.Value.ConcesionId.Should().Be(_concesionId);
        resuelta.Value.TenantObjetivoId.Should().Be(_tenantVisitado);
        resuelta.Value.Capacidad.Should().Be(CapacidadPrivilegio.SoporteLectura);
        resuelta.Value.UsuarioSimuladoId.Should().BeNull();
        resuelta.Value.PermiteEscritura.Should().BeFalse(
            "SoporteLectura es de solo lectura sin excepción implícita (ADR-011 § 4bis.2)");
    }

    [Fact]
    public async Task Cerrada_la_sesion_deja_de_resolver()
    {
        await ModificarAsync(async contexto =>
        {
            var sesion = await contexto.SesionesPrivilegiadas.FirstAsync(s => s.Id == _sesionId);
            sesion.Cerrar(DateTime.UtcNow);
        });

        (await ResolverAsync()).Should().BeNull();
    }

    [Fact]
    public async Task Vencida_la_ventana_de_la_sesion_deja_de_resolver_sin_que_nadie_la_cierre()
    {
        // Nadie la cerró: lo que pasó fue el tiempo. Si la caducidad se
        // comprobara solo al cerrarla, esta sesión seguiría abriendo el tenant.
        await ModificarAsync(async contexto =>
        {
            var sesion = await contexto.SesionesPrivilegiadas.FirstAsync(s => s.Id == _sesionId);
            contexto.Entry(sesion).Property(nameof(SesionPrivilegiada.ExpiraEnUtc))
                .CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        });

        (await ResolverAsync()).Should().BeNull();
    }

    /// <summary>
    /// El ataque central del incremento: la sesión sigue abierta y su ventana
    /// sigue siendo válida — lo que cambió está en la <b>concesión</b>, en otra
    /// tabla. Revocar tiene que cortar en el acto las sesiones ya abiertas bajo
    /// ella; si no, revocar no sirve para nada hasta que la ventana venza.
    /// </summary>
    [Fact]
    public async Task Revocada_la_concesion_la_sesion_ya_abierta_deja_de_resolver()
    {
        await ModificarAsync(async contexto =>
        {
            var concesion = await contexto.ConcesionesPrivilegio.FirstAsync(c => c.Id == _concesionId);
            concesion.Revocar(DateTime.UtcNow);
        });

        (await ResolverAsync()).Should().BeNull();

        // Y la sesión sigue abierta y en ventana: lo que corta es la concesión,
        // no un efecto colateral sobre la sesión.
        await using var comprobacion = CrearContexto();
        var sesionTrasRevocar = await comprobacion.SesionesPrivilegiadas.FirstAsync(s => s.Id == _sesionId);
        sesionTrasRevocar.EstaVigenteEn(DateTime.UtcNow).Should().BeTrue();
    }

    [Fact]
    public async Task Caducada_la_concesion_la_sesion_ya_abierta_deja_de_resolver()
    {
        // Igual que la anterior pero por vencimiento en vez de por revocación:
        // el estado sigue diciendo Vigente y solo la ventana ha pasado. Mirar
        // únicamente el estado dejaría esto vivo.
        await ModificarAsync(async contexto =>
        {
            var concesion = await contexto.ConcesionesPrivilegio.FirstAsync(c => c.Id == _concesionId);
            contexto.Entry(concesion).Property(nameof(ConcesionPrivilegio.VigenciaHasta))
                .CurrentValue = DateTime.UtcNow.AddMinutes(-1);
        });

        (await ResolverAsync()).Should().BeNull();
    }

    /// <summary>
    /// Recorte de alcance: la concesión sigue vigente y la sesión abierta, pero
    /// este tenant ya no está en su lista. Retirar un tenant tiene que cortar
    /// igual que revocar la concesión entera.
    /// </summary>
    [Fact]
    public async Task Retirado_el_tenant_del_alcance_la_sesion_deja_de_resolver()
    {
        await ModificarAsync(async contexto =>
        {
            var alcance = await contexto.TenantsAlcanzadosPorConcesion
                .FirstAsync(t => t.ConcesionPrivilegioId == _concesionId && t.TenantId == _tenantVisitado);
            contexto.TenantsAlcanzadosPorConcesion.Remove(alcance);
        });

        (await ResolverAsync()).Should().BeNull();
    }

    /// <summary>
    /// Sesión ajena: el identificador es real y la sesión está viva, pero la
    /// concesión que la ampara es de otro usuario de plataforma. Sin esta
    /// comprobación, conocer un id de sesión bastaría para heredarla.
    /// </summary>
    [Fact]
    public async Task Un_identificador_de_sesion_ajeno_no_le_sirve_a_otro_usuario()
    {
        (await ResolverAsync(usuarioId: Guid.NewGuid())).Should().BeNull();
    }

    /// <summary>
    /// Incoherencia entre los dos campos del token: dice abrir un tenant y
    /// nombra una sesión cuyo objetivo es otro. Preferir uno de los dos sería
    /// abrir un contexto que nadie autorizó; se descarta.
    /// </summary>
    [Fact]
    public async Task Un_token_que_declara_un_tenant_distinto_del_objetivo_de_la_sesion_no_resuelve()
    {
        (await ResolverAsync(tenantDelToken: _otroTenant)).Should().BeNull();
    }

    /// <summary>
    /// Identificador que nunca existió. Parece trivial y no lo es: una consulta
    /// escrita con un <c>LEFT JOIN</c> descuidado, o una comprobación que
    /// arrancara de la concesión en vez de la sesión, podría resolver algo aquí.
    /// </summary>
    [Fact]
    public async Task Una_sesion_que_no_existe_no_resuelve()
    {
        (await ResolverAsync(sesionId: Guid.NewGuid())).Should().BeNull();
    }

    /// <summary>
    /// La fila desaparece mientras la cookie sigue viva en el navegador. Es lo
    /// que pasaría con una purga o un borrado manual: el token conserva un
    /// identificador que ya no designa nada, y eso tiene que valer cero.
    /// </summary>
    [Fact]
    public async Task Borrada_la_fila_de_la_sesion_el_token_que_la_nombra_no_resuelve()
    {
        await ModificarAsync(async contexto =>
        {
            var sesion = await contexto.SesionesPrivilegiadas.FirstAsync(s => s.Id == _sesionId);
            contexto.SesionesPrivilegiadas.Remove(sesion);
        });

        (await ResolverAsync()).Should().BeNull();
    }

    /// <summary>
    /// Suplantación por sustitución de identificador: el atacante no puede
    /// forjar el token —va cifrado y firmado— pero sí podría intentar que su
    /// sesión legítima apunte a la concesión de otro. Se comprueba el caso
    /// simétrico al de arriba: existe una segunda concesión, de otro usuario,
    /// que sí cubre este tenant, y aun así la sesión sigue atada a la suya.
    /// </summary>
    [Fact]
    public async Task Una_concesion_ajena_que_cubre_el_mismo_tenant_no_rescata_una_sesion_de_otro()
    {
        var otroUsuario = Guid.NewGuid();

        await using (var contexto = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            contexto.ConcesionesPrivilegio.Add(ConcesionPrivilegio.SobreTenants(
                otroUsuario, CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4)));
            await contexto.SaveChangesAsync();
        }

        // La concesión propia se revoca; la ajena, viva, cubre el mismo tenant.
        await ModificarAsync(async contexto =>
        {
            var concesion = await contexto.ConcesionesPrivilegio.FirstAsync(c => c.Id == _concesionId);
            concesion.Revocar(DateTime.UtcNow);
        });

        (await ResolverAsync()).Should().BeNull(
            "la sesión se valida contra SU concesión, no contra cualquiera que cubra el tenant");

        // Y el otro usuario tampoco hereda esta sesión: su concesión existe,
        // pero no es la que la ampara.
        (await ResolverAsync(usuarioId: otroUsuario)).Should().BeNull();
    }

    /// <summary>
    /// Una sesión de impersonación con usuario simulado es una fila legítima
    /// —el dominio la admite bajo esa capacidad y solo bajo esa— y aun así no
    /// concede nada todavía: resuelve, pero sin escritura. El alcance que
    /// tampoco concede lo prueba <c>AlcanceDeSesionPrivilegiadaTests</c>.
    ///
    /// Importa porque la impersonación es la capacidad que más se parece a "ser
    /// otro usuario", y su camino de autorización —evaluar los planos 1 y 2 del
    /// simulado— no existe. Mientras no exista, resolver la sesión no puede
    /// traducirse en ningún permiso prestado.
    /// </summary>
    [Fact]
    public async Task Una_sesion_de_impersonacion_resuelve_pero_no_presta_los_permisos_del_simulado()
    {
        var usuarioImpersonador = Guid.NewGuid();
        var usuarioSimulado = Guid.NewGuid();
        Guid sesionId;

        await using (var contexto = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            var concesion = ConcesionPrivilegio.SobreTenants(
                usuarioImpersonador, CapacidadPrivilegio.Impersonacion, [_tenantVisitado],
                vigenciaDesde: ahora.AddMinutes(-5), vigenciaHasta: ahora.AddHours(1));

            var sesion = SesionPrivilegiada.Abrir(
                concesion, _tenantVisitado, "Reproducir lo que ve el usuario", ahora, TimeSpan.FromMinutes(30),
                usuarioSimuladoId: usuarioSimulado);

            contexto.ConcesionesPrivilegio.Add(concesion);
            contexto.SesionesPrivilegiadas.Add(sesion);
            await contexto.SaveChangesAsync();
            sesionId = sesion.Id;
        }

        var resuelta = await ResolverAsync(usuarioId: usuarioImpersonador, sesionId: sesionId);

        resuelta.Should().NotBeNull();
        resuelta!.Value.UsuarioSimuladoId.Should().Be(usuarioSimulado);
        resuelta.Value.PermiteEscritura.Should().BeFalse(
            "impersonar no es break-glass: sin el camino que evalúa los planos del simulado, escribir queda fuera");
    }

    [Fact]
    public async Task Sin_sesion_en_el_token_no_se_consulta_la_base()
    {
        // Coste cero para el 100 % de los usuarios de hoy: el contexto a null
        // lo demuestra — si la resolución tocara la base, esto reventaría.
        var resolutor = new SesionPrivilegiadaActual(
            contexto: null!,
            new ClienteActivoSeleccionadoFalso(_tenantVisitado, sesionPrivilegiadaId: null),
            new CurrentUserServiceFalso(_usuarioPlataforma));

        (await resolutor.ObtenerAsync()).Should().BeNull();
    }

    /// <summary>
    /// Una concesión global (solo <c>AdminPlataforma</c>) no tiene filas de
    /// alcance, así que la comprobación de alcance se salta — y tiene que
    /// seguir resolviendo. Se prueba porque la rama del <c>if</c> es
    /// precisamente la que un cambio descuidado dejaría sin cubrir.
    /// </summary>
    [Fact]
    public async Task Una_concesion_global_resuelve_sin_filas_de_alcance_y_tampoco_permite_escribir()
    {
        var usuarioAdmin = Guid.NewGuid();
        Guid sesionAdminId;

        await using (var contexto = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            var global = ConcesionPrivilegio.Global(
                usuarioAdmin, vigenciaDesde: ahora.AddMinutes(-5), vigenciaHasta: ahora.AddHours(1));
            var sesion = SesionPrivilegiada.Abrir(
                global, _otroTenant, "Diagnóstico de plataforma", ahora, TimeSpan.FromMinutes(30));

            contexto.ConcesionesPrivilegio.Add(global);
            contexto.SesionesPrivilegiadas.Add(sesion);
            await contexto.SaveChangesAsync();
            sesionAdminId = sesion.Id;
        }

        var resuelta = await ResolverAsync(
            usuarioId: usuarioAdmin, sesionId: sesionAdminId, tenantDelToken: _otroTenant);

        resuelta.Should().NotBeNull();
        resuelta!.Value.Capacidad.Should().Be(CapacidadPrivilegio.AdminPlataforma);
        resuelta.Value.PermiteEscritura.Should().BeFalse(
            "administrar la plataforma no es tocar los datos de un cliente (ADR-011 § 4bis.2)");
    }

    private async Task<SesionPrivilegiadaActiva?> ResolverAsync(
        Guid? usuarioId = null, Guid? sesionId = null, Guid? tenantDelToken = null)
    {
        await using var contexto = CrearContexto();

        var resolutor = new SesionPrivilegiadaActual(
            contexto,
            new ClienteActivoSeleccionadoFalso(tenantDelToken ?? _tenantVisitado, sesionId ?? _sesionId),
            new CurrentUserServiceFalso(usuarioId ?? _usuarioPlataforma));

        return await resolutor.ObtenerAsync();
    }

    private async Task ModificarAsync(Func<CaeManagerDbContext, Task> cambio)
    {
        await using var contexto = CrearContexto();
        await cambio(contexto);
        await contexto.SaveChangesAsync();
    }

    private CaeManagerDbContext CrearContexto()
    {
        // El tenant ambiental es el visitado, que es lo que hace una sesión de
        // soporte: abrir el contexto del cliente por la vía normal. Las tablas
        // del plano 3 son catálogos globales y quedan fuera del filtro, pero el
        // interceptor de sellado sigue montado — como en producción.
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantVisitado };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .AddInterceptors(new TenantSelladoInterceptor(tenantActual))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }

    private sealed class ClienteActivoSeleccionadoFalso(Guid? tenantId, Guid? sesionPrivilegiadaId)
        : IClienteActivoSeleccionado
    {
        public Guid? TenantIdSeleccionado => tenantId;

        // Excluyentes por construcción: un token con las dos vías se descarta
        // entero antes de llegar aquí (ver ClienteActivoSeleccionado).
        public Guid? AsignacionOperacionIdSeleccionada => null;

        public Guid? SesionPrivilegiadaIdSeleccionada => sesionPrivilegiadaId;
    }
}

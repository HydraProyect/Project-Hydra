using CaeManager.Application.Plataforma;
using CaeManager.Application.Plataforma.Commands.AbrirSesionPrivilegiada;
using CaeManager.Domain.Plataforma;
using CaeManager.Infrastructure.MultiTenancy;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Plataforma;
using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CaeManager.IntegrationTests.Plataforma;

/// <summary>
/// La ceremonia de apertura, precondición por precondición.
///
/// <b>Cada una tiene su propio test y su propio código de error</b>, y eso es el
/// punto: colapsarlas en un "no autorizado" genérico dejaría que la desaparición
/// de una pasara inadvertida. Si mañana alguien borra la comprobación de 2FA, se
/// pone rojo el test de 2FA y solo ese; con una condición única, los demás
/// seguirían verdes y el agregado parecería sano.
///
// El <b>orden</b> también se comprueba, y no por rendimiento: 2FA y "el objetivo
/// es ajeno" dependen solo de quién pide y sobre qué, así que van delante; la
/// consulta de concesiones viene después, para que quien no supera esas dos no
/// obtenga señales sobre qué concesiones existen.
///
/// <para>
/// Desde A0 <b>la concesión es la fuente de la autoridad</b>, no un requisito
/// más de la lista: pertenecer al tenant marcado como plataforma dejó de ser
/// suficiente y dejó de ser necesario para abrir. Eso tiene aquí dos tests
/// dedicados — el que demuestra que la pertenencia no basta, y el que demuestra
/// que no hace falta.
/// </para>
/// </summary>
public class AbrirSesionPrivilegiadaTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _otroTenant = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();

    private Guid _concesionSobreVisitado;

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();

        contexto.Tenants.Add(CrearTenantDePlataforma());

        var ahora = DateTime.UtcNow;
        var concesion = ConcesionPrivilegio.SobreTenants(
            _tecnico, CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
            vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));

        contexto.ConcesionesPrivilegio.Add(concesion);
        await contexto.SaveChangesAsync();

        _concesionSobreVisitado = concesion.Id;
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── Camino feliz ───────────────────────────────────────────────────────

    [Fact]
    public async Task Con_las_tres_precondiciones_la_sesion_se_abre()
    {
        // Control positivo. Sin él, todos los tests de abajo pasarían igual si
        // el comando denegara siempre.
        var resultado = await EjecutarAsync(_tenantVisitado);

        resultado.EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var sesion = await contexto.SesionesPrivilegiadas.SingleAsync();
        sesion.TenantObjetivoId.Should().Be(_tenantVisitado);
        sesion.Motivo.Should().Be("Reproducir la incidencia");
        sesion.EstaAbierta.Should().BeTrue();
    }

    // ── Precondición 1: autoridad para abrir ───────────────────────────────

    /// <summary>
    /// <b>El test decisivo de A0.</b> Es el que distingue haber convertido
    /// <c>EsPlataforma</c> en raíz de bootstrap de haberle cambiado el nombre.
    ///
    /// <para>
    /// Las dos ejecuciones comparten usuario, tenant de origen —el de
    /// plataforma— y doble factor. Lo <b>único</b> que cambia entre ellas es si
    /// existe una concesión, y el resultado se invierte: la autoridad efectiva
    /// es la concesión y no la pertenencia.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Pertenecer_al_tenant_de_plataforma_no_basta_para_abrir_sin_concesion()
    {
        var sinConcesion = await EjecutarAsync(_tenantVisitado, concesionId: Guid.NewGuid());
        var conConcesion = await EjecutarAsync(_tenantVisitado);

        sinConcesion.EsFallido.Should().BeTrue(
            "pertenecer al tenant de plataforma dejó de ser suficiente para iniciar la ceremonia");
        conConcesion.EsExitoso.Should().BeTrue(
            "y lo que la habilita es la concesión: mismo usuario, misma casa, mismo 2FA");
    }

    /// <summary>
    /// La otra mitad: la pertenencia tampoco es <b>necesaria</b>. Lo que este
    /// test demuestra es que el comando no la exige — no que exista hoy un
    /// camino productivo por el que alguien de fuera de la plataforma llegue a
    /// tener una concesión. No lo hay: el único emisor es la auto-concesión, y
    /// su puerta es la raíz de bootstrap. La autoridad se decide al CONCEDER, y
    /// por eso la ceremonia no vuelve a preguntarla.
    /// </summary>
    [Fact]
    public async Task Un_usuario_de_fuera_de_la_plataforma_con_concesion_valida_abre()
    {
        var resultado = await EjecutarAsync(_tenantVisitado, tenantOrigen: _otroTenant);

        resultado.EsExitoso.Should().BeTrue(
            "la autoridad la porta la concesión; el comando no pregunta de qué tenant es quien la esgrime");
    }

    [Fact]
    public async Task Nadie_abre_una_sesion_de_soporte_sobre_su_propio_tenant()
    {
        // Restricción que la vía heredada no hacía explícita: sobre el tenant
        // propio ya se entra por la vía normal, y abrir una sesión privilegiada
        // ahí sería una forma de saltarse el propio rol dentro de la
        // organización.
        var resultado = await EjecutarAsync(_tenantPlataforma);

        // Código propio desde A0. La regla vivía dentro de la autorización de
        // apertura que el incremento retira; si se hubiera dado por incluida en
        // la nueva política de capacidad, habría desaparecido sin que ningún
        // test lo notara — el resto de la batería habría seguido en verde.
        resultado.Error.Codigo.Should().Be("SesionPrivilegiada.TenantPropio");
        await NoHayNingunaSesionAsync();
    }

    // ── Precondición 2: doble factor ───────────────────────────────────────

    [Fact]
    public async Task Sin_doble_factor_activo_no_se_abre_nada()
    {
        // Se conserva de la ceremonia heredada: quien entra en datos de un
        // cliente ajeno con una cuenta protegida solo por contraseña es
        // exactamente el escenario que este acceso existe para contener.
        var resultado = await EjecutarAsync(_tenantVisitado, dobleFactor: false);

        resultado.Error.Codigo.Should().Be("SesionPrivilegiada.SinDobleFactor");
        await NoHayNingunaSesionAsync();
    }

    // ── Precondición 3: la concesión ───────────────────────────────────────

    [Fact]
    public async Task Una_concesion_inexistente_no_abre_nada()
    {
        var resultado = await EjecutarAsync(_tenantVisitado, concesionId: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be("SesionPrivilegiada.ConcesionNoEncontrada");
        await NoHayNingunaSesionAsync();
    }

    /// <summary>
    /// Concesión <b>ajena</b>: existe, cubre el tenant objetivo, y es de otro
    /// usuario de plataforma. El comando la rechaza.
    ///
    /// <b>Lo que este test demuestra, exactamente.</b> La suite conecta como
    /// superusuario, así que RLS no se evalúa: la concesión ajena SÍ es visible
    /// para la consulta. Por eso el rechazo sólo puede venir de la comprobación
    /// de propiedad en el comando. Es decir, el que en otros contextos era un
    /// defecto del instrumento —la conexión de superusuario— aquí es justo lo
    /// que permite aislar la segunda barrera y probarla sola.
    ///
    /// <b>Lo que NO demuestra.</b> No demuestra que la aplicación proteja frente
    /// a una RLS desactivada: sólo blinda la <i>propiedad de la concesión</i>.
    /// Un rol con <c>BYPASSRLS</c> seguiría invalidando el resto de fronteras de
    /// aislamiento. Que la primera barrera exista y funcione lo prueba
    /// <c>RlsPlanoPrivilegioTests</c>, contra el rol restringido.
    /// </summary>
    [Fact]
    public async Task Una_concesion_de_otro_usuario_no_sirve_aunque_la_consulta_llegue_a_verla()
    {
        Guid concesionAjena;
        await using (var contexto = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            var deOtro = ConcesionPrivilegio.SobreTenants(
                Guid.NewGuid(), CapacidadPrivilegio.SoporteLectura, [_tenantVisitado],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));

            contexto.ConcesionesPrivilegio.Add(deOtro);
            await contexto.SaveChangesAsync();
            concesionAjena = deOtro.Id;
        }

        var resultado = await EjecutarAsync(_tenantVisitado, concesionId: concesionAjena);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SesionPrivilegiada.ConcesionNoEncontrada",
            "el mismo error que si no existiera: la respuesta observable no debe distinguir 'no existe' de " +
            "'existe pero no es tuya'");

        await NoHayNingunaSesionAsync();
    }

    // ── Precondición 4: la capacidad concedida habilita la ceremonia ───────

    /// <summary>
    /// Una concesión vigente, del usuario correcto y que cubre el tenant
    /// objetivo, pero de una capacidad que no está en la lista de apertura.
    /// Si esto abriera, <c>SoporteLectura</c> —y cualquier capacidad futura—
    /// se convertiría en llave de la ceremonia por el mero hecho de existir.
    /// </summary>
    [Fact]
    public async Task Una_capacidad_que_no_habilita_la_apertura_no_abre_nada()
    {
        Guid concesionDeOtraCapacidad;
        await using (var contexto = CrearContexto())
        {
            var ahora = DateTime.UtcNow;
            var otraCapacidad = ConcesionPrivilegio.SobreTenants(
                _tecnico, CapacidadPrivilegio.Impersonacion, [_tenantVisitado],
                vigenciaDesde: ahora.AddMinutes(-10), vigenciaHasta: ahora.AddHours(4));

            contexto.ConcesionesPrivilegio.Add(otraCapacidad);
            await contexto.SaveChangesAsync();
            concesionDeOtraCapacidad = otraCapacidad.Id;
        }

        var resultado = await EjecutarAsync(_tenantVisitado, concesionId: concesionDeOtraCapacidad);

        resultado.Error.Codigo.Should().Be("SesionPrivilegiada.CapacidadNoAbreSesion",
            "la capacidad DE la sesión no es la capacidad para ABRIRLA");

        await NoHayNingunaSesionAsync();
    }

    // ── El cruce: concesión de A, objetivo B ───────────────────────────────

    [Fact]
    public async Task Una_concesion_sobre_un_tenant_no_sirve_para_abrir_sobre_otro()
    {
        // La frontera donde se encuentran tres cosas: autorización de apertura,
        // tenant de origen y alcance de la concesión. El Id de la concesión
        // llega como entrada del comando, así que hay que demostrar que ese Id
        // no se convierte en autoridad sobre un tenant que la concesión no
        // cubre.
        //
        // Aquí la concesión SÍ es visible —es del propio técnico— así que no la
        // esconde RLS: la rechaza el dominio, porque CubreEn comprueba estado,
        // ventana y alcance juntos. El error lo distingue: NoAbrible, no
        // ConcesionNoEncontrada.
        var resultado = await EjecutarAsync(_otroTenant);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("SesionPrivilegiada.NoAbrible");
        await NoHayNingunaSesionAsync();
    }

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Domain.Common.Result<Guid>> EjecutarAsync(
        Guid tenantObjetivo,
        Guid? tenantOrigen = null,
        Guid? concesionId = null,
        bool dobleFactor = true)
    {
        await using var contexto = CrearContexto();

        var currentUser = new CurrentUserServiceFalso(
            _tecnico, rol: null, tenantOrigenId: tenantOrigen ?? _tenantPlataforma, dobleFactor);

        var handler = new AbrirSesionPrivilegiadaCommandHandler(
            contexto,
            new PlataformaWriter(contexto),
            currentUser,
            contexto);

        return await handler.Handle(
            new AbrirSesionPrivilegiadaCommand(
                concesionId ?? _concesionSobreVisitado, tenantObjetivo, "Reproducir la incidencia", DiasDeVentana: 1),
            CancellationToken.None);
    }

    private async Task NoHayNingunaSesionAsync()
    {
        await using var contexto = CrearContexto();
        (await contexto.SesionesPrivilegiadas.CountAsync()).Should().Be(0,
            "una precondición que falla no puede dejar una sesión a medio abrir");
    }

    private Domain.Tenants.Tenant CrearTenantDePlataforma()
    {
        var tenant = new Domain.Tenants.Tenant("Plataforma de pruebas");
        typeof(Domain.Common.Entity).GetProperty(nameof(Domain.Common.Entity.Id))!
            .SetValue(tenant, _tenantPlataforma);
        tenant.MarcarComoPlataforma();
        return tenant;
    }

    private CaeManagerDbContext CrearContexto()
    {
        var tenantActual = new TenantActualAmbiental { TenantId = _tenantPlataforma };
        var options = new DbContextOptionsBuilder<CaeManagerDbContext>()
            .UseNpgsql(_cadenaConexion, npgsql => npgsql.MigrationsAssembly("CaeManager.Migrations.PostgreSQL"))
            .Options;

        return new CaeManagerDbContext(options, new EphemeralDataProtectionProvider(), tenantActual);
    }
}

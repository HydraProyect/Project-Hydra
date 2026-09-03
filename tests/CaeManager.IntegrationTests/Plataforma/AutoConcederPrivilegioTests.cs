using System.Reflection;
using CaeManager.Application.Plataforma.Commands.AutoConcederPrivilegio;
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
/// Auto-concesión: el acto explícito que hace ejercitable la ceremonia de
/// apertura sin abrir todavía la semántica de delegar privilegios a terceros.
///
/// <b>La garantía principal de este comando no se comprueba, se construye.</b>
/// No existe ningún parámetro para el beneficiario: sale de la sesión. Por eso
/// "yo → otro" no es un caso rechazado sino un caso <i>irrepresentable</i>, y el
/// test que lo afirma mira la forma del comando, no su comportamiento — un test
/// de comportamiento solo podría probar los beneficiarios que se le ocurran al
/// que lo escribe.
/// </summary>
public class AutoConcederPrivilegioTests : IAsyncLifetime
{
    private readonly string _cadenaConexion = BaseDatosPostgresDePruebas.CadenaConexionUnica();
    private readonly Guid _tenantPlataforma = Guid.NewGuid();
    private readonly Guid _tenantVisitado = Guid.NewGuid();
    private readonly Guid _tecnico = Guid.NewGuid();

    public async Task InitializeAsync()
    {
        await using var contexto = CrearContexto();
        await contexto.Database.MigrateAsync();
        contexto.Tenants.Add(CrearTenantDePlataforma());

        // Desde A2 la autoridad no la da el tenant sino la identidad raíz
        // designada por el despliegue. Sin esta fila no hay bootstrap posible, y
        // eso también se comprueba (ver Sin_raiz_designada_nadie_arranca_nada).
        contexto.EstadoBootstrapPlataforma.Add(
            EstadoBootstrapPlataforma.Designar(_tecnico, DateTime.UtcNow));

        await contexto.SaveChangesAsync();
    }

    public Task DisposeAsync() => BaseDatosPostgresDePruebas.EliminarAsync(_cadenaConexion);

    // ── La garantía estructural ────────────────────────────────────────────

    [Fact]
    public void El_comando_no_admite_beneficiario_asi_que_conceder_a_otro_es_irrepresentable()
    {
        // Si alguien añadiera un parámetro de usuario a este comando, dejaría de
        // ser auto-concesión y pasaría a ser la operación genérica de conceder
        // —quién concede, a quién, qué capacidad, cómo se revoca— que es un
        // contrato propio y que además exigiría relajar el WITH CHECK de RLS.
        // Este test hace que ese cambio tenga que ser deliberado.
        var parametros = typeof(AutoConcederPrivilegioCommand)
            .GetConstructors().Single()
            .GetParameters().Select(p => p.Name).ToList();

        parametros.Should().BeEquivalentTo(["TenantObjetivoId", "Capacidad", "DiasDeVigencia"],
            "el beneficiario sale de la sesión; en cuanto sea un parámetro, esto deja de ser auto-concesión");

        typeof(AutoConcederPrivilegioCommand).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .Should().NotContain(n => n.Contains("Usuario", StringComparison.OrdinalIgnoreCase));
    }

    // ── Comportamiento ─────────────────────────────────────────────────────

    [Fact]
    public async Task El_usuario_se_concede_a_si_mismo_y_queda_registrada_la_autoria()
    {
        // Desde A2, emitir SoporteLectura exige AdminPlataforma vigente: la
        // cadena es raíz → fundacional → soporte. El contrato que este test
        // verifica no cambia; lo que cambia es de dónde sale la autoridad.
        (await ArrancarAsync()).EsExitoso.Should().BeTrue();

        var resultado = await EjecutarAsync();

        resultado.EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var concesion = await contexto.ConcesionesPrivilegio
            .Include(c => c.TenantsAlcanzados)
            .SingleAsync(c => c.Capacidad == CapacidadPrivilegio.SoporteLectura);

        concesion.UsuarioPlataformaId.Should().Be(_tecnico, "yo → yo");
        concesion.ConcedidaPorUsuarioId.Should().Be(_tecnico,
            "la autoría se registra desde el primer día, aunque hoy coincida con el beneficiario");
        concesion.Capacidad.Should().Be(CapacidadPrivilegio.SoporteLectura);
        concesion.EsAlcanceGlobal.Should().BeFalse("una auto-concesión nunca es global");
        concesion.TenantsAlcanzados.Should().ContainSingle()
            .Which.TenantId.Should().Be(_tenantVisitado);
    }

    [Fact]
    public async Task La_concesion_creada_habilita_de_verdad_la_apertura()
    {
        // Desde A2, emitir SoporteLectura exige AdminPlataforma vigente: la
        // cadena es raíz → fundacional → soporte. El contrato que este test
        // verifica no cambia; lo que cambia es de dónde sale la autoridad.
        (await ArrancarAsync()).EsExitoso.Should().BeTrue();

        // El circuito completo, que es la razón de que esta operación entre en
        // F2b-6: sin ella la ceremonia quedaba formalmente implementada y
        // operacionalmente huérfana.
        var concesionId = (await EjecutarAsync()).Valor;

        await using var contexto = CrearContexto();
        var concesion = await contexto.ConcesionesPrivilegio
            .Include(c => c.TenantsAlcanzados)
            .SingleAsync(c => c.Id == concesionId);

        var abrir = () => SesionPrivilegiada.Abrir(
            concesion, _tenantVisitado, "Reproducir la incidencia", DateTime.UtcNow, TimeSpan.FromHours(1));

        abrir.Should().NotThrow("auto-concederse y abrir tienen que encadenar");
    }

    [Fact]
    public async Task Sin_autoridad_de_plataforma_no_se_concede_nada()
    {
        var resultado = await EjecutarAsync(tenantOrigen: Guid.NewGuid());

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.NoAutorizado");
        await NoHayNingunaConcesionAsync();
    }

    [Fact]
    public async Task Sin_doble_factor_no_se_concede_nada()
    {
        // Desde A2, emitir SoporteLectura exige AdminPlataforma vigente: la
        // cadena es raíz → fundacional → soporte. El contrato que este test
        // verifica no cambia; lo que cambia es de dónde sale la autoridad.
        (await ArrancarAsync()).EsExitoso.Should().BeTrue();

        // La ceremonia se comprueba en cada paso que CREA autoridad, no solo al
        // abrir: si no, quedaría un camino para dejar la concesión preparada sin
        // segundo factor y usarla después.
        var resultado = await EjecutarAsync(dobleFactor: false);

        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.SinDobleFactor");
        await NoHayNingunaConcesionAsync();
    }

    [Fact]
    public async Task Nadie_se_concede_privilegio_sobre_su_propio_tenant()
    {
        // Desde A2, emitir SoporteLectura exige AdminPlataforma vigente: la
        // cadena es raíz → fundacional → soporte. El contrato que este test
        // verifica no cambia; lo que cambia es de dónde sale la autoridad.
        (await ArrancarAsync()).EsExitoso.Should().BeTrue();

        var resultado = await EjecutarAsync(tenantObjetivo: _tenantPlataforma);

        // Código propio desde A0, por lo mismo que al abrir: la regla venía
        // dentro de la autorización de apertura retirada y necesita test que la
        // distinga de "no eres la raíz".
        resultado.Error.Codigo.Should().Be("ConcesionPrivilegio.TenantPropio");
        await NoHayNingunaConcesionAsync();
    }

    // ── A2: la cadena raíz → AdminPlataforma → SoporteLectura ─────────────

    /// <summary>
    /// El acto fundacional. Los cuatro rasgos van juntos y ninguno es parámetro:
    /// capacidad, alcance, beneficiario y origen salen de la fábrica del dominio.
    /// </summary>
    [Fact]
    public async Task La_raiz_designada_arranca_la_plataforma_y_consume_el_bootstrap()
    {
        var resultado = await ArrancarAsync();

        resultado.EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var concesion = await contexto.ConcesionesPrivilegio.SingleAsync();

        concesion.Capacidad.Should().Be(CapacidadPrivilegio.AdminPlataforma);
        concesion.EsAlcanceGlobal.Should().BeTrue();
        concesion.UsuarioPlataformaId.Should().Be(_tecnico);
        concesion.Origen.Should().Be(OrigenConcesion.BootstrapPlataforma,
            "reconocer la fundacional por su forma no discrimina: Global() obliga a AdminPlataforma, " +
            "así que toda concesión global futura tendría el mismo aspecto");

        var estado = await contexto.EstadoBootstrapPlataforma.SingleAsync();
        estado.Consumido.Should().BeTrue();
    }

    /// <summary>
    /// La propiedad central de A2: el bootstrap es un acto único, no una
    /// capacidad permanente de acuñar autoridad.
    /// </summary>
    [Fact]
    public async Task El_bootstrap_no_se_puede_ejecutar_dos_veces()
    {
        (await ArrancarAsync()).EsExitoso.Should().BeTrue();

        var segundo = await ArrancarAsync();

        segundo.EsFallido.Should().BeTrue();
        segundo.Error.Codigo.Should().Be("ConcesionPrivilegio.NoAutorizado");

        await using var contexto = CrearContexto();
        (await contexto.ConcesionesPrivilegio.CountAsync()).Should().Be(1);
    }

    /// <summary>
    /// Y no se reabre al perder la concesión fundacional. Si lo hiciera,
    /// tendríamos una autoridad de emergencia permanente escondida tras la
    /// ausencia de una fila.
    /// </summary>
    [Fact]
    public async Task Revocar_la_concesion_fundacional_no_reabre_el_bootstrap()
    {
        (await ArrancarAsync()).EsExitoso.Should().BeTrue();

        await using (var contexto = CrearContexto())
        {
            var concesion = await contexto.ConcesionesPrivilegio.SingleAsync();
            concesion.Revocar(DateTime.UtcNow);
            await contexto.SaveChangesAsync();
        }

        var reintento = await ArrancarAsync();

        reintento.EsFallido.Should().BeTrue(
            "consumido es consumido: la recuperación es un procedimiento administrativo externo");
    }

    [Fact]
    public async Task Quien_no_es_la_raiz_no_arranca_nada()
    {
        var resultado = await ArrancarAsync(usuario: Guid.NewGuid());

        resultado.EsFallido.Should().BeTrue();
        await NoHayNingunaConcesionAsync();
    }

    /// <summary>
    /// Antes de A2 la autoridad era pertenecer al tenant de plataforma — que es
    /// también el tenant operativo de la empresa, así que cualquier gestor podía
    /// acuñarse autoridad. Este test fija que eso ya no ocurre.
    /// </summary>
    [Fact]
    public async Task Pertenecer_al_tenant_de_plataforma_no_basta_para_arrancar()
    {
        var otroMiembroDelMismoTenant = Guid.NewGuid();

        var resultado = await ArrancarAsync(usuario: otroMiembroDelMismoTenant);

        resultado.EsFallido.Should().BeTrue(
            "la raíz es una persona designada por el despliegue, no una organización");
    }

    /// <summary>
    /// La cadena completa: sin AdminPlataforma no hay emisión de soporte, y con
    /// ella sí. Esto es lo que evita que A2 rompa F2b-6.
    /// </summary>
    [Fact]
    public async Task Solo_con_AdminPlataforma_vigente_se_puede_uno_conceder_SoporteLectura()
    {
        var antes = await EjecutarAsync();
        antes.EsFallido.Should().BeTrue("sin AdminPlataforma no hay autoridad para emitir soporte");

        (await ArrancarAsync()).EsExitoso.Should().BeTrue();

        var despues = await EjecutarAsync();
        despues.EsExitoso.Should().BeTrue();

        await using var contexto = CrearContexto();
        var soporte = await contexto.ConcesionesPrivilegio
            .SingleAsync(c => c.Capacidad == CapacidadPrivilegio.SoporteLectura);

        soporte.Origen.Should().Be(OrigenConcesion.Ordinaria,
            "solo el acto fundacional es BootstrapPlataforma");
    }

    [Fact]
    public async Task Sin_raiz_designada_nadie_arranca_nada()
    {
        await using (var contexto = CrearContexto())
        {
            contexto.EstadoBootstrapPlataforma.RemoveRange(contexto.EstadoBootstrapPlataforma);
            await contexto.SaveChangesAsync();
        }

        var resultado = await ArrancarAsync();

        resultado.EsFallido.Should().BeTrue("un despliegue sin raíz designada no habilita a nadie");
    }

    /// <summary>
    /// Atomicidad de las dos escrituras. Dos arranques simultáneos compiten por
    /// el mismo bootstrap: uno gana y el otro pierde contra el token de
    /// concurrencia del estado. Lo que se comprueba es que <b>el perdedor no
    /// deja nada detrás</b> — si la concesión y el consumo no viajaran en el
    /// mismo SaveChanges, quedaría una concesión fundacional huérfana con el
    /// bootstrap todavía abierto, es decir, dos raíces posibles.
    /// </summary>
    [Fact]
    public async Task Dos_arranques_simultaneos_dejan_exactamente_una_concesion_fundacional()
    {
        var barrera = new Barrier(2);

        // Task.Run NO es una regla general para tests asíncronos: aquí hace falta
        // porque Barrier.SignalAndWait() BLOQUEA el hilo y la barrera necesita dos
        // participantes físicos distintos.
        //
        // Sin él esto era un interbloqueo determinista: un método async se ejecuta
        // en el hilo del llamante hasta su primer await, así que la primera
        // invocación se quedaba en SignalAndWait() esperando a un segundo
        // participante que nunca llegaba a crearse — porque quien tenía que
        // crearlo era el hilo recién bloqueado.
        Task<bool> IntentarAsync() => Task.Run(async () =>
        {
            barrera.SignalAndWait();
            try
            {
                return (await ArrancarAsync()).EsExitoso;
            }
            catch (DbUpdateException)
            {
                // El perdedor de la carrera. Que reviente es correcto; lo que
                // importa es qué dejó persistido, y eso se mira abajo.
                return false;
            }
        });

        var resultados = await Task.WhenAll(IntentarAsync(), IntentarAsync());

        resultados.Count(ok => ok).Should().Be(1, "el bootstrap es un acto único");

        await using var contexto = CrearContexto();
        var estado = await contexto.EstadoBootstrapPlataforma.SingleAsync();
        var fundacionales = await contexto.ConcesionesPrivilegio
            .CountAsync(c => c.Origen == OrigenConcesion.BootstrapPlataforma);

        // El PAR tiene que ser coherente, no cada mitad por su lado. Contar solo
        // concesiones no distinguiría el caso en que el perdedor hubiera
        // persistido el consumo sin la concesión: quedarían una fundacional y un
        // consumo, exactamente igual que el estado correcto.
        fundacionales.Should().Be(estado.Consumido ? 1 : 0,
            "concesión fundacional y consumo del bootstrap van en el mismo SaveChanges: o las dos " +
            "cosas o ninguna, nunca una combinación intermedia");

        estado.Consumido.Should().BeTrue("el ganador sí persistió las dos");
        fundacionales.Should().Be(1);
    }

    /// <summary>
    /// La identidad raíz no se reasigna, y no por disciplina del seeder: la fila
    /// es única por clave primaria canónica, así que designar una segunda raíz
    /// choca contra la base.
    ///
    /// <para>
    /// Esto cubre a <b>cualquier</b> llamante, no solo al arranque. La otra
    /// mitad —que el agregado no ofrece forma de mutar <c>UsuarioRaizId</c>— la
    /// fija un test de arquitectura por reflexión.
    /// </para>
    /// </summary>
    [Fact]
    public async Task Designar_una_segunda_raiz_choca_contra_la_base()
    {
        await using var contexto = CrearContexto();
        contexto.EstadoBootstrapPlataforma.Add(
            EstadoBootstrapPlataforma.Designar(Guid.NewGuid(), DateTime.UtcNow));

        var intento = async () => await contexto.SaveChangesAsync();

        await intento.Should().ThrowAsync<DbUpdateException>(
            "la unicidad de la raíz la garantiza la clave primaria, no una comprobación leída antes " +
            "en memoria que dos arranques simultáneos pasarían a la vez");
    }

    private Task<Domain.Common.Result<Guid>> ArrancarAsync(Guid? usuario = null) =>
        EjecutarAsync(
            tenantObjetivo: Guid.Empty,
            capacidad: CapacidadPrivilegio.AdminPlataforma,
            usuario: usuario);

    // ── Andamiaje ──────────────────────────────────────────────────────────

    private async Task<Domain.Common.Result<Guid>> EjecutarAsync(
        Guid? tenantObjetivo = null, Guid? tenantOrigen = null, bool dobleFactor = true,
        CapacidadPrivilegio? capacidad = null, Guid? usuario = null)
    {
        await using var contexto = CrearContexto();

        var currentUser = new CurrentUserServiceFalso(
            usuario ?? _tecnico, rol: null, tenantOrigenId: tenantOrigen ?? _tenantPlataforma, dobleFactor);

        var handler = new AutoConcederPrivilegioCommandHandler(
            new PlataformaWriter(contexto),
            new AutorizacionAutoConcesionPorMatriz(
                new RaizBootstrapPorIdentidadDesignada(contexto), contexto),
            contexto,
            currentUser,
            contexto);

        return await handler.Handle(
            new AutoConcederPrivilegioCommand(
                tenantObjetivo ?? _tenantVisitado,
                capacidad ?? CapacidadPrivilegio.SoporteLectura,
                DiasDeVigencia: 7),
            CancellationToken.None);
    }

    private async Task NoHayNingunaConcesionAsync()
    {
        await using var contexto = CrearContexto();
        (await contexto.ConcesionesPrivilegio
            .CountAsync(c => c.Capacidad == CapacidadPrivilegio.SoporteLectura)).Should().Be(0);
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

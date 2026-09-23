using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Domain.Plataforma;
using CaeManager.Domain.Common;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

public class AutorizacionEscrituraBehaviorTests
{
    // Lo que decide es la interfaz ICommand, no el sufijo del nombre — ver
    // AutorizacionEscrituraBehavior y el test de al lado que lo demuestra.
    private record FalsoCommand : ICommand;
    private record FalsoConValorCommand : ICommand<Guid>;
    private record FalsaQuery : IRequest<string>;

    // Se llama "Command" pero no implementa ICommand: el behavior lo deja
    // pasar. Es exactamente el agujero que antes abría un typo en el nombre,
    // solo que ahora al revés — y quien lo cierra es ArquitecturaCommandsTests,
    // que falla si un tipo *Command del ensamblado de Application no
    // implementa ICommand. Este test existe para dejar constancia de que la
    // red de seguridad es ese test de arquitectura, no el behavior.
    private record FalsoSinInterfazCommand : IRequest<Result>;

    // PD-A3: comando marcado, para distinguir de FalsoCommand en las pruebas
    // de la tercera condición.
    private record FalsoComandoDeAprovisionamiento : ICommand, IComandoDeAprovisionamiento;

    // Autoservicio: escribe solo datos del propio usuario.
    private record FalsoComandoDeAutoservicio : ICommand, IComandoDeAutoservicio;
    private record FalsoComandoDeAutoservicioConValor : ICommand<Guid>, IComandoDeAutoservicio;

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Bloquea_un_command_con_resultado_simple_para_roles_de_solo_lectura(string rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesionPrivilegiada, SinTenant);
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new FalsoCommand(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        siguienteFueLlamado.Should().BeFalse();
    }

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    public async Task Bloquea_un_command_con_resultado_generico_para_roles_de_solo_lectura(string rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoConValorCommand, Result<Guid>>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesionPrivilegiada, SinTenant);

        var resultado = await behavior.Handle(
            new FalsoConValorCommand(), _ => Task.FromResult(Result.Exito(Guid.NewGuid())), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("GestorCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("DireccionCae")]
    public async Task Deja_pasar_un_command_para_roles_con_permiso_de_escritura(string rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesionPrivilegiada, SinTenant);

        var resultado = await behavior.Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RolInventado")]
    public async Task Bloquea_un_command_cuando_no_hay_un_rol_de_escritura_reconocible(string? rol)
    {
        // Lista blanca, no lista negra (hallazgo N-5 de INFORME-AUDITORIA-2.md).
        // "Sin rol" ocurre de verdad en dos casos: un usuario que aún no lo
        // tiene asignado, y un Operador Delegado cuya delegación se revocó
        // mientras su token de selección seguía vigente — ahí
        // ObtenerRolActualAsync devuelve null a propósito.
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesionPrivilegiada, SinTenant);
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new FalsoCommand(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        siguienteFueLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task No_bloquea_una_query_ni_siquiera_para_roles_de_solo_lectura()
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsaQuery, string>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"), SinSesionPrivilegiada, SinTenant);

        var resultado = await behavior.Handle(new FalsaQuery(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
    }

    [Fact]
    public async Task Un_tipo_llamado_Command_sin_ICommand_no_lo_autoriza_el_behavior()
    {
        // No es el comportamiento deseable, es el comportamiento real: el
        // behavior ya no mira nombres. Quien impide que esto exista en el
        // código de producción es ArquitecturaCommandsTests.
        var behavior = new AutorizacionEscrituraBehavior<FalsoSinInterfazCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"), SinSesionPrivilegiada, SinTenant);

        var resultado = await behavior.Handle(
            new FalsoSinInterfazCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    // ── Autoservicio: lo propio del usuario, con cualquier rol reconocido ──

    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData("GestorCae")]
    public async Task Un_comando_de_autoservicio_pasa_con_cualquier_rol_reconocido(string rol)
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoComandoDeAutoservicio, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesionPrivilegiada, SinTenant);
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new FalsoComandoDeAutoservicio(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
        siguienteFueLlamado.Should().BeTrue();
    }

    [Fact]
    public async Task Un_comando_de_autoservicio_con_resultado_generico_tambien_pasa_para_Consulta()
    {
        var behavior = new AutorizacionEscrituraBehavior<FalsoComandoDeAutoservicioConValor, Result<Guid>>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"), SinSesionPrivilegiada, SinTenant);

        var resultado = await behavior.Handle(
            new FalsoComandoDeAutoservicioConValor(), _ => Task.FromResult(Result.Exito(Guid.NewGuid())), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("RolInventado")]
    public async Task Un_comando_de_autoservicio_sigue_bloqueado_sin_rol_reconocible(string? rol)
    {
        // La exención es de la lista de escritura, no de la lista blanca: el caso
        // del Operador Delegado con la delegación revocada (rol null) sigue cerrado.
        var behavior = new AutorizacionEscrituraBehavior<FalsoComandoDeAutoservicio, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), rol), SinSesionPrivilegiada, SinTenant);
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new FalsoComandoDeAutoservicio(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SoloLectura");
        siguienteFueLlamado.Should().BeFalse();
    }

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task Una_sesion_privilegiada_no_escribe_ni_un_comando_de_autoservicio(CapacidadPrivilegio capacidad)
    {
        // El soporte de plataforma no acepta términos ni guarda filtros en nombre de
        // nadie: bajo impersonación, la aceptación quedaría atribuida al usuario
        // simulado. La rama de sesión privilegiada decide antes que el marcador.
        var behavior = new AutorizacionEscrituraBehavior<FalsoComandoDeAutoservicio, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Consulta"),
            new SesionPrivilegiadaActualFalsa(SesionCon(capacidad)), SinTenant);
        var siguienteFueLlamado = false;

        var resultado = await behavior.Handle(new FalsoComandoDeAutoservicio(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        siguienteFueLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task Una_sesion_de_aprovisionamiento_no_escribe_un_comando_de_autoservicio()
    {
        var tenantObjetivo = Guid.NewGuid();
        var behavior = new AutorizacionEscrituraBehavior<FalsoComandoDeAutoservicio, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenantObjetivo)),
            new TenantActualFalso(tenantObjetivo));

        var resultado = await behavior.Handle(
            new FalsoComandoDeAutoservicio(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.ComandoFueraDelAprovisionamiento");
    }

    // ── Plano 3: sesiones privilegiadas de plataforma ──────────────────────

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    public async Task Una_sesion_privilegiada_sin_capacidad_de_escritura_bloquea_todo_command(
        CapacidadPrivilegio capacidad)
    {
        // El rol se pone a "Administrador" a propósito: es el peor caso real —
        // un técnico de TALVEG que es Administrador en SU tenant abriendo el de
        // un cliente. Si la denegación dependiera del rol, este test pasaría de
        // largo; depende de la vía de acceso, que es lo correcto.
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(SesionCon(capacidad)), SinTenant);

        var siguienteFueLlamado = false;
        var resultado = await behavior.Handle(new FalsoCommand(), _ =>
        {
            siguienteFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");
        siguienteFueLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task BreakGlass_tambien_bloquea_mientras_no_exista_su_camino_de_escritura()
    {
        // BreakGlass sí tiene capacidad de escritura en el modelo, pero lo que
        // la hace aceptable —motivo, ventana, traza y revisión posterior— es
        // una fase propia que todavía no está construida. Denegar es lo
        // correcto hasta entonces, y con un código distinto para que se vea que
        // es una fase pendiente y no la regla permanente de solo lectura.
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.BreakGlass)), SinTenant);

        var resultado = await behavior.Handle(
            new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.BreakGlassSinCaminoDeEscritura");
    }

    // ── PD-A3, commit 4: las tres condiciones de Aprovisionamiento ─────────

    [Fact]
    public async Task Aprovisionamiento_deja_pasar_un_comando_marcado_dentro_del_tenant_objetivo()
    {
        var tenantObjetivo = Guid.NewGuid();
        var sesion = SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenantObjetivo);
        var behavior = new AutorizacionEscrituraBehavior<FalsoComandoDeAprovisionamiento, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(sesion), new TenantActualFalso(tenantObjetivo));

        var resultado = await behavior.Handle(
            new FalsoComandoDeAprovisionamiento(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    [Fact]
    public async Task Aprovisionamiento_bloquea_un_comando_no_marcado()
    {
        var tenantObjetivo = Guid.NewGuid();
        var sesion = SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenantObjetivo);
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(sesion), new TenantActualFalso(tenantObjetivo));

        var resultado = await behavior.Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.ComandoFueraDelAprovisionamiento",
            "un comando fuera de la lista explícita no se beneficia de la escritura acotada, aunque el " +
            "resto de condiciones se cumplan");
    }

    [Fact]
    public async Task Aprovisionamiento_bloquea_un_comando_marcado_fuera_del_tenant_objetivo()
    {
        var tenantObjetivo = Guid.NewGuid();
        var otroTenant = Guid.NewGuid();
        var sesion = SesionCon(CapacidadPrivilegio.Aprovisionamiento, tenantObjetivo);
        var behavior = new AutorizacionEscrituraBehavior<FalsoComandoDeAprovisionamiento, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(sesion), new TenantActualFalso(otroTenant));

        var resultado = await behavior.Handle(
            new FalsoComandoDeAprovisionamiento(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsFallido.Should().BeTrue();
        resultado.Error.Codigo.Should().Be("Autorizacion.AprovisionamientoFueraDelTenantObjetivo",
            "aunque el comando esté marcado, la sesión de aprovisionamiento no puede escribir fuera del " +
            "tenant que la ampara — ni siquiera en el tenant donde el técnico esté navegando de más");
    }

    [Fact]
    public async Task Una_sesion_privilegiada_no_bloquea_las_queries()
    {
        // El soporte es de solo LECTURA, no de nada: si esto bloqueara, la
        // capacidad SoporteLectura no serviría para lo único que existe.
        var behavior = new AutorizacionEscrituraBehavior<FalsaQuery, string>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "Administrador"),
            new SesionPrivilegiadaActualFalsa(SesionCon(CapacidadPrivilegio.SoporteLectura)), SinTenant);

        var resultado = await behavior.Handle(new FalsaQuery(), _ => Task.FromResult("ok"), CancellationToken.None);

        resultado.Should().Be("ok");
    }

    // ── REC-067: el punto de mutación no puede confiar en la memo de circuito ──

    /// <summary>
    /// El ataque del handoff HO-067-01: una sesión de soporte válida al abrir
    /// el circuito, pero su concesión se revoca a mitad de circuito —antes de
    /// la SEGUNDA escritura, no de la primera—. Si el behavior reutilizara el
    /// resultado que vio en la primera escritura (como hacía antes de
    /// REC-067), la segunda seguiría denegando con el código de "hay sesión
    /// privilegiada" aunque esa sesión ya no exista. Revalidando fresco, la
    /// segunda escritura ve que la sesión ya no resuelve y cae al camino de
    /// rol de siempre — que deniega igual, pero por el motivo correcto: no hay
    /// sesión privilegiada que lo impida, es que un plano 3 no tiene rol de
    /// negocio (mismo resultado final, procedencia distinta, y es esa
    /// procedencia la que hay que revalidar antes de que una escritura de
    /// Aprovisionamiento dependa de esta misma comprobación).
    /// </summary>
    [Fact]
    public async Task Revoca_la_concesion_entre_dos_escrituras_del_mismo_circuito_y_la_segunda_revalida_fresca()
    {
        var resolutor = new SesionPrivilegiadaConMemoDeCircuito(SesionCon(CapacidadPrivilegio.SoporteLectura));
        var currentUser = new CurrentUserServiceFalso(Guid.NewGuid(), null);

        var primeraEscritura = await new AutorizacionEscrituraBehavior<FalsoCommand, Result>(currentUser, resolutor, SinTenant)
            .Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        primeraEscritura.Error.Codigo.Should().Be("Autorizacion.SesionPrivilegiadaSoloLectura");

        resolutor.Revocar();

        var segundaEscritura = await new AutorizacionEscrituraBehavior<FalsoCommand, Result>(currentUser, resolutor, SinTenant)
            .Handle(new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        segundaEscritura.EsFallido.Should().BeTrue();
        segundaEscritura.Error.Codigo.Should().Be(
            "Autorizacion.SoloLectura",
            "tras revocar, RevalidarAsync debe ver que ya no hay sesión — si el behavior usara el " +
            "resultado memoizado de ObtenerAsync, seguiría devolviendo Autorizacion.SesionPrivilegiadaSoloLectura");
    }

    [Fact]
    public async Task Sin_sesion_privilegiada_el_rol_sigue_mandando()
    {
        // Guarda de no regresión: la comprobación nueva no puede cambiar el
        // camino de los usuarios normales, que son todos los de hoy.
        var behavior = new AutorizacionEscrituraBehavior<FalsoCommand, Result>(
            new CurrentUserServiceFalso(Guid.NewGuid(), "GestorCae"), SinSesionPrivilegiada, SinTenant);

        var resultado = await behavior.Handle(
            new FalsoCommand(), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    private static SesionPrivilegiadaActiva SesionCon(CapacidadPrivilegio capacidad, Guid? tenantObjetivoId = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), tenantObjetivoId ?? Guid.NewGuid(), capacidad, null);

    private static readonly ISesionPrivilegiadaActual SinSesionPrivilegiada =
        new SesionPrivilegiadaActualFalsa(null);

    private static readonly ITenantActual SinTenant = new TenantActualFalso(null);

    private sealed class TenantActualFalso(Guid? tenantId) : ITenantActual
    {
        public Guid? TenantId => tenantId;
    }

    private sealed class SesionPrivilegiadaActualFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        // El behavior de escritura consulta este método, no ObtenerAsync — ver
        // Revoca_la_concesion_entre_dos_escrituras_del_mismo_circuito_y_la_segunda_revalida_fresca.
        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }

    /// <summary>
    /// Doble que distingue las dos vías del REC-067: <c>ObtenerAsync</c> se
    /// queda congelado en la sesión vista la primera vez —simula el ámbito de
    /// DI compartido por un circuito de Blazor Server, donde memoiza— y
    /// <c>RevalidarAsync</c> lee siempre el estado "real" actual, que el test
    /// muta entre dos escrituras llamando a <see cref="Revocar"/>. Si el
    /// behavior usara <c>ObtenerAsync</c> en vez de <c>RevalidarAsync</c>, la
    /// segunda escritura seguiría viendo la sesión ya revocada.
    /// </summary>
    private sealed class SesionPrivilegiadaConMemoDeCircuito(SesionPrivilegiadaActiva sesionInicial) : ISesionPrivilegiadaActual
    {
        private readonly SesionPrivilegiadaActiva? _memoDeLectura = sesionInicial;
        private SesionPrivilegiadaActiva? _estadoReal = sesionInicial;

        public void Revocar() => _estadoReal = null;

        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_memoDeLectura);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_estadoReal);
    }
}

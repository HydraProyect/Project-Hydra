using CaeManager.Application.Common;
using CaeManager.Application.Centros.Commands.CrearCanalGestion;
using CaeManager.Application.Centros.Commands.EditarCanalGestion;
using CaeManager.Application.Empresas.Commands.BorrarCredencialAccesoEmpresaContrasena;
using CaeManager.Application.Empresas.Commands.GuardarCredencialAccesoEmpresa;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Subcontratas.Commands.GuardarCredencialAccesoSubcontrata;
using CaeManager.Domain.Common;
using CaeManager.Domain.Plataforma;
using FluentAssertions;
using MediatR;
using Xunit;

namespace CaeManager.Application.Tests.Common;

/// <summary>
/// El escalón "¿recurso permitido?" del plano 3: una sesión de
/// <c>SoporteLectura</c> ve el tenant entero, y aun así no puede llevarse las
/// contraseñas de las plataformas externas del cliente.
///
/// La distinción no es de grado sino de naturaleza. Un documento del cliente es
/// un dato que se inspecciona y cuya lectura queda auditada. Una contraseña de
/// un portal de terceros es autoridad sobre otro sistema: sigue funcionando
/// cuando la sesión se cierre, fuera de TALVEG, donde nuestra auditoría no
/// llega. Ninguna de las cuatro capacidades del plano 3 la incluye — tampoco
/// break-glass, que puede escribir en los datos del cliente pero no quedarse
/// con sus llaves.
/// </summary>
public class AutorizacionSecretosDeTenantBehaviorTests
{
    private record CredencialDto(string Usuario, string Contrasena);

    private record ConsultaDeCredencialQuery
        : IRequest<CredencialDto?>, IConsultaDeSecretosDeTenant;

    private record ConsultaNormalQuery : IRequest<string?>;

    private record ConsultaDeDatosDeCredencialQuery : IRequest<CredencialDto?>, IConsultaDeDatosDeCredencial;

    private static readonly CredencialDto Secreto = new("admin@cliente", "hunter2");

    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task Ninguna_capacidad_del_plano_3_obtiene_los_secretos_del_tenant(CapacidadPrivilegio capacidad)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SesionCon(capacidad), UsuarioConRol("Administrador"));

        var handlerFueLlamado = false;
        var resultado = await behavior.Handle(new ConsultaDeCredencialQuery(), _ =>
        {
            handlerFueLlamado = true;
            return Task.FromResult<CredencialDto?>(Secreto);
        }, CancellationToken.None);

        // Con un rol que SÍ lee secretos: el null solo puede venir de la sesión
        // privilegiada, no de la regla de roles.
        resultado.Should().BeNull();

        // El handler ni siquiera corre: la credencial no llega a descifrarse,
        // así que no pasa por memoria ni por ningún log del camino.
        handlerFueLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task Sin_sesion_privilegiada_la_consulta_de_credenciales_funciona_con_normalidad()
    {
        // Guarda de no regresión: quien gestiona de verdad esas plataformas es
        // el Gestor CAE del Operador CAE, y para él no cambia nada.
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SinSesion, UsuarioConRol("GestorCae"));

        var resultado = await behavior.Handle(
            new ConsultaDeCredencialQuery(), _ => Task.FromResult<CredencialDto?>(Secreto), CancellationToken.None);

        resultado.Should().Be(Secreto);
    }

    [Fact]
    public async Task Una_consulta_sin_marcar_no_se_toca_ni_bajo_sesion_privilegiada()
    {
        // Es lo que hace útil a SoporteLectura: todo lo demás del tenant sí se
        // ve. Si esto denegara, el incremento no habría introducido una
        // capacidad de inspección sino una pantalla en blanco.
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaNormalQuery, string?>(
            SesionCon(CapacidadPrivilegio.SoporteLectura), UsuarioConRol("Administrador"));

        var resultado = await behavior.Handle(
            new ConsultaNormalQuery(), _ => Task.FromResult<string?>("datos del tenant"), CancellationToken.None);

        resultado.Should().Be("datos del tenant");
    }

    [Fact]
    public async Task Una_consulta_marcada_que_devolviera_un_tipo_por_valor_falla_ruidosamente()
    {
        // No es un caso de ejecución sino un error de programación: denegar
        // devolviendo default(int) sería inventarse un 0 que el consumidor
        // interpretaría como un dato. Mejor romper en el primer test que lo
        // toque que devolver un valor falso en producción.
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, int>(
            SesionCon(CapacidadPrivilegio.SoporteLectura), UsuarioConRol("Administrador"));

        var accion = async () => await behavior.Handle(
            new ConsultaDeCredencialQuery(), _ => Task.FromResult(42), CancellationToken.None);

        await accion.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tipo por valor*");
    }

    /// <summary>
    /// Decisión del propietario (2026-09-23, opción A): los secretos del Tenant
    /// propietario solo los leen los roles con escritura. Consulta es de solo
    /// lectura —también la Consulta delegada del Operador CAE externo, cuyo rol
    /// llega ya resuelto por el workspace delegado— y una credencial no es un
    /// dato que se mira, es la llave para actuar en la plataforma CAE en nombre
    /// del Tenant propietario. Cliente (usuario de portal) y un rol sin resolver
    /// tampoco: fallo cerrado.
    /// </summary>
    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    [InlineData("RolQueNoExiste")]
    public async Task Un_rol_sin_escritura_no_obtiene_los_secretos_del_tenant(string? rol)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SinSesion, UsuarioConRol(rol));

        var handlerFueLlamado = false;
        var resultado = await behavior.Handle(new ConsultaDeCredencialQuery(), _ =>
        {
            handlerFueLlamado = true;
            return Task.FromResult<CredencialDto?>(Secreto);
        }, CancellationToken.None);

        resultado.Should().BeNull();
        handlerFueLlamado.Should().BeFalse("la credencial no llega ni a descifrarse");
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Un_rol_con_escritura_obtiene_los_secretos_del_tenant(string rol)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SinSesion, UsuarioConRol(rol));

        var resultado = await behavior.Handle(
            new ConsultaDeCredencialQuery(), _ => Task.FromResult<CredencialDto?>(Secreto), CancellationToken.None);

        resultado.Should().Be(Secreto);
    }

    // P1-I1: el rol que lee secretos los lee solo con el 2FA activo. La
    // denegación lanza, para que la pantalla pueda llevar a configurarlo, y el
    // handler no llega a ejecutarse (ni descifra ni deja rastro de lectura).
    [Theory]
    [InlineData("Administrador")]
    [InlineData("DireccionCae")]
    [InlineData("CoordinadorCae")]
    [InlineData("GestorCae")]
    public async Task Un_rol_con_escritura_sin_2FA_no_obtiene_los_secretos_del_tenant(string rol)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SinSesion, UsuarioSinDobleFactor(rol));

        var handlerFueLlamado = false;
        var accion = () => behavior.Handle(new ConsultaDeCredencialQuery(), _ =>
        {
            handlerFueLlamado = true;
            return Task.FromResult<CredencialDto?>(Secreto);
        }, CancellationToken.None);

        await accion.Should().ThrowAsync<SegundoFactorRequeridoParaCredencialesException>();
        handlerFueLlamado.Should().BeFalse();
    }

    [Theory]
    [InlineData("Administrador")]
    [InlineData("GestorCae")]
    public async Task Un_rol_con_escritura_sin_2FA_tampoco_obtiene_los_datos_de_una_credencial(string rol)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeDatosDeCredencialQuery, CredencialDto?>(
            SinSesion, UsuarioSinDobleFactor(rol));

        var handlerFueLlamado = false;
        var accion = () => behavior.Handle(new ConsultaDeDatosDeCredencialQuery(), _ =>
        {
            handlerFueLlamado = true;
            return Task.FromResult<CredencialDto?>(Secreto);
        }, CancellationToken.None);

        await accion.Should().ThrowAsync<SegundoFactorRequeridoParaCredencialesException>();
        handlerFueLlamado.Should().BeFalse();
    }

    // Un rol que no lee secretos recibe el null de siempre, tenga o no 2FA:
    // invitarle a activarlo le prometería un acceso que su rol no le da.
    [Theory]
    [InlineData("Consulta")]
    [InlineData("Cliente")]
    [InlineData(null)]
    public async Task Un_rol_sin_escritura_y_sin_2FA_recibe_null_no_la_invitacion_a_activarlo(string? rol)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SinSesion, UsuarioSinDobleFactor(rol));

        var resultado = await behavior.Handle(
            new ConsultaDeCredencialQuery(), _ => Task.FromResult<CredencialDto?>(Secreto), CancellationToken.None);

        resultado.Should().BeNull();
    }

    // En una Sesión Privilegiada nada cambia: denegado con null, antes de mirar el 2FA.
    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task En_sesion_privilegiada_sin_2FA_sigue_siendo_null(CapacidadPrivilegio capacidad)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeCredencialQuery, CredencialDto?>(
            SesionCon(capacidad), UsuarioSinDobleFactor("Administrador"));

        var resultado = await behavior.Handle(
            new ConsultaDeCredencialQuery(), _ => Task.FromResult<CredencialDto?>(Secreto), CancellationToken.None);

        resultado.Should().BeNull();
    }

    [Fact]
    public async Task Una_consulta_sin_marcar_no_exige_2FA()
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaNormalQuery, string?>(
            SinSesion, UsuarioSinDobleFactor("GestorCae"));

        var resultado = await behavior.Handle(
            new ConsultaNormalQuery(), _ => Task.FromResult<string?>("dato normal"), CancellationToken.None);

        resultado.Should().Be("dato normal");
    }

    [Fact]
    public async Task Una_consulta_sin_marcar_no_se_toca_para_un_rol_de_solo_lectura()
    {
        // Consulta sigue viendo todo lo demás del Tenant: la regla es sobre
        // secretos, no una pantalla en blanco para el rol de lectura.
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaNormalQuery, string?>(
            SinSesion, UsuarioConRol("Consulta"));

        var resultado = await behavior.Handle(
            new ConsultaNormalQuery(), _ => Task.FromResult<string?>("datos del tenant"), CancellationToken.None);

        resultado.Should().Be("datos del tenant");
    }

    /// <summary>
    /// El usuario de una credencial sin su contraseña (la precarga del
    /// formulario de edición) sigue la misma regla de roles: Consulta, propia o
    /// delegada, no lo lee.
    /// </summary>
    [Theory]
    [InlineData("Consulta", false)]
    [InlineData("Cliente", false)]
    [InlineData(null, false)]
    [InlineData("GestorCae", true)]
    [InlineData("Administrador", true)]
    public async Task Los_datos_de_una_credencial_solo_los_obtiene_un_rol_con_escritura(string? rol, bool obtiene)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeDatosDeCredencialQuery, CredencialDto?>(
            SinSesion, UsuarioConRol(rol));

        var resultado = await behavior.Handle(
            new ConsultaDeDatosDeCredencialQuery(), _ => Task.FromResult<CredencialDto?>(Secreto), CancellationToken.None);

        resultado.Should().Be(obtiene ? Secreto : null);
    }

    /// <summary>
    /// La precarga tampoco se entrega en una Sesión Privilegiada (opción D,
    /// 2026-09-23): toda lectura efectiva se audita, y en esa sesión la
    /// conexión no puede escribir la fila de auditoría. No hay riesgo de
    /// read-modify-write: en esa sesión los comandos que guardan una credencial
    /// se deniegan (AutorizacionEscrituraBehavior), así que el null nunca llega a
    /// guardarse como credencial vacía.
    /// </summary>
    [Theory]
    [InlineData(CapacidadPrivilegio.SoporteLectura)]
    [InlineData(CapacidadPrivilegio.Impersonacion)]
    [InlineData(CapacidadPrivilegio.AdminPlataforma)]
    [InlineData(CapacidadPrivilegio.BreakGlass)]
    public async Task Los_datos_de_una_credencial_tampoco_se_entregan_en_sesion_privilegiada(CapacidadPrivilegio capacidad)
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<ConsultaDeDatosDeCredencialQuery, CredencialDto?>(
            SesionCon(capacidad), UsuarioConRol("Administrador"));

        var handlerFueLlamado = false;
        var resultado = await behavior.Handle(new ConsultaDeDatosDeCredencialQuery(), _ =>
        {
            handlerFueLlamado = true;
            return Task.FromResult<CredencialDto?>(Secreto);
        }, CancellationToken.None);

        resultado.Should().BeNull();
        handlerFueLlamado.Should().BeFalse();
    }

    /// <summary>
    /// P1-I2, hueco declarado en #900: quien puede fijar la llave de una
    /// plataforma de terceros puede sustituirla por una que conozca. Escribirla
    /// exige el mismo 2FA que leerla, y el handler no llega a correr.
    /// </summary>
    [Fact]
    public async Task Escribir_datos_de_credencial_sin_2FA_lanza_la_excepcion_y_no_llega_al_handler()
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<EscrituraDeCredencialCommand, Result>(
            SinSesion, UsuarioSinDobleFactor("Administrador"));

        var handlerFueLlamado = false;
        var accion = async () => await behavior.Handle(new EscrituraDeCredencialCommand(true), _ =>
        {
            handlerFueLlamado = true;
            return Task.FromResult(Result.Exito());
        }, CancellationToken.None);

        await accion.Should().ThrowAsync<SegundoFactorRequeridoParaCredencialesException>();
        handlerFueLlamado.Should().BeFalse();
    }

    [Fact]
    public async Task Escribir_datos_de_credencial_con_2FA_llega_al_handler()
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<EscrituraDeCredencialCommand, Result>(
            SinSesion, UsuarioConRol("GestorCae"));

        var resultado = await behavior.Handle(
            new EscrituraDeCredencialCommand(true), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    /// <summary>
    /// Un canal de gestión sin usuario ni contraseña, o editado sin cambiarlas,
    /// no escribe nada que proteger: exigir 2FA ahí bloquearía dar de alta un
    /// buzón de correo sin ganar nada.
    /// </summary>
    [Fact]
    public async Task Un_Command_marcado_que_no_toca_la_credencial_no_exige_2FA()
    {
        var behavior = new AutorizacionSecretosDeTenantBehavior<EscrituraDeCredencialCommand, Result>(
            SinSesion, UsuarioSinDobleFactor("GestorCae"));

        var resultado = await behavior.Handle(
            new EscrituraDeCredencialCommand(false), _ => Task.FromResult(Result.Exito()), CancellationToken.None);

        resultado.EsExitoso.Should().BeTrue();
    }

    public static TheoryData<IBaseRequest, bool> CommandsRealesDeCredencial => new()
    {
        { new GuardarCredencialAccesoEmpresaCommand(Guid.NewGuid(), null, null, "u", null), true },
        { new GuardarCredencialAccesoSubcontrataCommand(Guid.NewGuid(), null, null, null, null), true },
        { new BorrarCredencialAccesoEmpresaContrasenaCommand(Guid.NewGuid()), true },
        { new CrearCanalGestionCommand(Guid.NewGuid(), default, "x", null, null, "u", null, null, null, null), true },
        { new CrearCanalGestionCommand(Guid.NewGuid(), default, "x", null, null, null, "p", null, null, null), true },
        { new CrearCanalGestionCommand(Guid.NewGuid(), default, "x", null, null, null, null, null, null, null), false },
        { new EditarCanalGestionCommand(Guid.NewGuid(), "x", null, null, null, null, null, CambiarCredenciales: true), true },
        { new EditarCanalGestionCommand(Guid.NewGuid(), "x", null, null, null, null, null, CambiarCredenciales: false, Usuario: "u"), false },
    };

    /// <summary>
    /// Los Commands de verdad, no el de prueba: cuáles escriben datos de
    /// credencial y cuándo. Guardar sin contraseña también cuenta (fija el
    /// usuario); borrar la contraseña también (es cambiar la llave).
    /// </summary>
    [Theory]
    [MemberData(nameof(CommandsRealesDeCredencial))]
    public void Los_Commands_de_credencial_declaran_cuando_escriben_datos_de_credencial(IBaseRequest command, bool escribe)
    {
        command.Should().BeAssignableTo<IEscrituraDeDatosDeCredencial>();
        ((IEscrituraDeDatosDeCredencial)command).EscribeDatosDeCredencial.Should().Be(escribe);
    }

    /// <summary>
    /// Detector para el siguiente Command: todo Command o Query de Application con
    /// una propiedad <c>Contrasena</c> escribe una credencial y debe llevar la
    /// marca. La contraseña de una cuenta de usuario no pasa por Application con
    /// ese nombre; si algún día lo hace, este test obliga a decidirlo.
    /// </summary>
    [Fact]
    public void Todo_Command_con_Contrasena_lleva_la_marca_de_escritura_de_credencial()
    {
        var conContrasena = typeof(IEscrituraDeDatosDeCredencial).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(ICommandBase).IsAssignableFrom(t))
            .Where(t => t.GetProperty("Contrasena") is not null)
            .ToList();

        // Control positivo: si el detector deja de ver los cuatro que existen hoy,
        // ha dejado de observar y la comprobación de abajo pasaría en vacío.
        conContrasena.Should().Contain(
        [
            typeof(GuardarCredencialAccesoEmpresaCommand), typeof(GuardarCredencialAccesoSubcontrataCommand),
            typeof(CrearCanalGestionCommand), typeof(EditarCanalGestionCommand),
        ]);

        conContrasena.Where(t => !typeof(IEscrituraDeDatosDeCredencial).IsAssignableFrom(t))
            .Should().BeEmpty("un Command que escribe una contraseña de plataforma exige 2FA (P1-I2)");
    }

    private record EscrituraDeCredencialCommand(bool Escribe) : ICommand, IEscrituraDeDatosDeCredencial
    {
        bool IEscrituraDeDatosDeCredencial.EscribeDatosDeCredencial => Escribe;
    }

    private static ISesionPrivilegiadaActual SesionCon(CapacidadPrivilegio capacidad) =>
        new SesionPrivilegiadaActualFalsa(
            new SesionPrivilegiadaActiva(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), capacidad, null));

    private static readonly ISesionPrivilegiadaActual SinSesion = new SesionPrivilegiadaActualFalsa(null);

    private static ICurrentUserService UsuarioConRol(string? rol) => new CurrentUserServiceFalso(rol, dobleFactorActivo: true);

    private static ICurrentUserService UsuarioSinDobleFactor(string? rol) => new CurrentUserServiceFalso(rol, dobleFactorActivo: false);

    private sealed class CurrentUserServiceFalso(string? rol, bool dobleFactorActivo) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult(rol);
        public Task<string?> ObtenerRolOrigenAsync() => ObtenerRolEfectivoAsync();
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(Guid.NewGuid());
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(dobleFactorActivo);
    }

    private sealed class SesionPrivilegiadaActualFalsa(SesionPrivilegiadaActiva? sesion) : ISesionPrivilegiadaActual
    {
        public Task<SesionPrivilegiadaActiva?> ObtenerAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);

        public Task<SesionPrivilegiadaActiva?> RevalidarAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(sesion);
    }
}

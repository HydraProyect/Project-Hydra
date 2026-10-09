using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Application.Usuarios.Commands.CorregirCorreoCuentaPendiente;
using CaeManager.Application.Usuarios.Commands.GenerarActivacionUsuario;
using CaeManager.Domain.Auditoria;
using CaeManager.Infrastructure.Autorizacion;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using CaeManager.Infrastructure.Persistence.Seed;
using CaeManager.IntegrationTests.Arranque;
using FluentAssertions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit;

namespace CaeManager.IntegrationTests.Usuarios;

/// <summary>
/// El enlace de activación de una cuenta pendiente, contra PostgreSQL real con el rol
/// <c>cae_app_runtime</c> (<see cref="ArnesDeArranqueRuntime"/>), con el handler real y
/// <see cref="GestionCuentasUsuarioIdentity"/>, no con dobles. El enlace es una credencial
/// al portador —quien lo tiene fija la contraseña—, así que aquí se mide lo que solo
/// Identity y la base pueden decir: que emitir uno nuevo deja sin valor el anterior
/// (canjeándolo de verdad con <c>ResetPasswordAsync</c>, que es lo que hace la página
/// anónima) y que cada emisión queda en la auditoría de la cuenta con su Actor real.
/// Quién puede pedirlo está en <c>GestionCuentasCommandsTests</c> (Application).
/// </summary>
public class EnlaceDeActivacionBajoRuntimeTests
{
    private static readonly Guid TenantA = TenantSeedData.IdPorDefecto;
    private const string ContrasenaNueva = "Arnes#2026Seguro";

    [Fact]
    public async Task Reenviar_la_activacion_deja_sin_valor_el_enlace_anterior()
    {
        var adminId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, pendienteId, "pendiente@caemanager.local", contrasena: null);

        var primero = await EmitirAsync(arnes, pendienteId);
        var segundo = await EmitirAsync(arnes, pendienteId);

        var conElPrimero = await CanjearAsync(arnes, pendienteId, primero);
        conElPrimero.Succeeded.Should().BeFalse("solo vale el último enlace emitido");
        conElPrimero.Errors.Should().ContainSingle(e => e.Code == "InvalidToken");
        (await TieneContrasenaAsync(arnes.CadenaPropietario, pendienteId)).Should().BeFalse(
            "el enlace anterior no llegó a fijar ninguna contraseña");

        var conElSegundo = await CanjearAsync(arnes, pendienteId, segundo);
        conElSegundo.Succeeded.Should().BeTrue(
            "control positivo: el canje funciona en este arnés, así que el rechazo de arriba es del enlace. Errores: "
            + string.Join(", ", conElSegundo.Errors.Select(e => e.Code)));
    }

    [Fact]
    public async Task Cada_emision_queda_en_la_auditoria_de_la_cuenta_con_su_Actor_real_y_sin_el_sello()
    {
        var adminId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, pendienteId, "pendiente@caemanager.local", contrasena: null);
        var antes = DateTime.UtcNow.AddSeconds(-5);

        await EmitirAsync(arnes, pendienteId);
        await EmitirAsync(arnes, pendienteId);

        var filas = await LeerEmisionesAsync(arnes.CadenaPropietario, pendienteId);
        filas.Should().HaveCount(2, "una fila por emisión, no una por cuenta");
        filas.Should().OnlyContain(f => f.TenantId == TenantA);
        filas.Should().OnlyContain(f => f.UsuarioId == adminId && f.ActorRealUsuarioId == adminId,
            "quien emite es el Administrador, no la cuenta afectada");
        filas.Should().OnlyContain(f => f.FechaUtc >= antes);

        var sello = await LeerSelloAsync(arnes.CadenaPropietario, pendienteId);
        sello.Should().NotBeNullOrEmpty("premisa: hay un sello que podría haberse filtrado");
        filas.Should().OnlyContain(f => f.DatosAntes!.Contains("\"SecurityStamp\":\"***\"")
                                        && f.DatosDespues!.Contains("\"SecurityStamp\":\"***\""),
            "la fila dice que el sello cambió, nunca cuál es");
        filas.Should().NotContain(f => f.DatosDespues!.Contains(sello!));
    }

    /// <summary>
    /// ADR-011 § 8.4: una emisión hecha simulando a alguien tiene que distinguirse de una
    /// que hizo esa persona. La fila la escribe <c>AuditoriaInterceptor</c> con el actor
    /// que le dé <see cref="IActorAuditoria"/>; aquí se comprueba que la acción propia de
    /// la emisión no pierde esa separación.
    /// </summary>
    [Fact]
    public async Task En_una_impersonacion_la_emision_separa_Actor_real_de_Usuario_simulado()
    {
        var actorReal = Guid.NewGuid();
        var simulado = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(
            new ActorAuditoria(actorReal, simulado, TipoViaAcceso.Normal, null), simulado);
        await CrearCuentaAsync(arnes, simulado, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, actorReal, "soporte@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, pendienteId, "pendiente@caemanager.local", contrasena: null);

        await EmitirAsync(arnes, pendienteId);

        var fila = (await LeerEmisionesAsync(arnes.CadenaPropietario, pendienteId)).Should().ContainSingle().Subject;
        fila.UsuarioId.Should().Be(simulado);
        fila.ActorRealUsuarioId.Should().Be(actorReal);
    }

    /// <summary>
    /// Revisión puente de la PR: si la persona fija su contraseña entre la lectura de la
    /// cuenta y la escritura del sello, el <c>UpdateAsync</c> falla por concurrencia
    /// cuando el interceptor ya había añadido la fila de auditoría al contexto. Ni esa
    /// fila ni la cuenta con el sello cambiado en memoria pueden quedarse en el
    /// <c>DbContext</c> del circuito: el siguiente guardado de cualquier pantalla las
    /// arrastraría. La carrera se provoca de verdad, con un validador que activa la
    /// cuenta por otra conexión en mitad del <c>UpdateAsync</c>.
    /// </summary>
    [Fact]
    public async Task Si_la_persona_se_activa_durante_la_emision_no_queda_auditoria_ni_restos_en_el_contexto()
    {
        var adminId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, pendienteId, "pendiente@caemanager.local", contrasena: null);

        using var circuito = arnes.Servicios.CreateScope();
        var sp = circuito.ServiceProvider;
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        var usuarios = UserManagerQueSufreLaCarrera(
            sp, () => ActivarPorOtraConexionAsync(arnes.CadenaPropietario, pendienteId));
        var puerta = new PuertaAccesoDatos();
        var puerto = new GestionCuentasUsuarioIdentity(
            usuarios, puerta,
            new DirectorioUsuariosTenant(usuarios, contexto, sp.GetRequiredService<ITenantActual>(), puerta, contexto),
            contexto);

        var emision = await puerto.GenerarTokenActivacionAsync(pendienteId);

        emision.EsFallido.Should().BeTrue("la cuenta dejó de estar pendiente en mitad de la emisión");
        emision.Error.Codigo.Should().Be("Usuarios.FalloAlEmitirActivacion",
            "control positivo: el fallo es el de la escritura del sello, no otro anterior");
        var selloTrasLaCarrera = await LeerSelloAsync(arnes.CadenaPropietario, pendienteId);

        var siguienteGuardadoDelCircuito = () => contexto.SaveChangesAsync();
        await siguienteGuardadoDelCircuito.Should().NotThrowAsync(
            "la cuenta que no se pudo guardar no se queda modificada en el contexto");
        (await LeerEmisionesAsync(arnes.CadenaPropietario, pendienteId)).Should().BeEmpty(
            "no se emitió ningún enlace, así que no hay emisión que auditar");
        (await LeerSelloAsync(arnes.CadenaPropietario, pendienteId)).Should().Be(selloTrasLaCarrera,
            "el sello que se cambió en memoria no llega a la base en un guardado posterior");
    }

    // ---------- Corregir el correo de una cuenta pendiente ----------

    [Fact]
    public async Task Corregir_el_correo_deja_sin_valor_el_enlace_enviado_al_correo_anterior()
    {
        var adminId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, pendienteId, "mal-escrito@caemanager.local", contrasena: null);
        var enviadoAlCorreoAnterior = await EmitirAsync(arnes, pendienteId);

        var correccion = await CorregirAsync(arnes, pendienteId, "bien-escrito@caemanager.local");

        correccion.EsExitoso.Should().BeTrue(correccion.EsFallido ? correccion.Error.Mensaje : "");
        (await LeerCorreoAsync(arnes.CadenaPropietario, pendienteId)).Should().Be(new CorreoDeCuenta(
            "bien-escrito@caemanager.local", "bien-escrito@caemanager.local",
            "BIEN-ESCRITO@CAEMANAGER.LOCAL", "BIEN-ESCRITO@CAEMANAGER.LOCAL"),
            "correo y nombre de usuario son el mismo dato, y por los normalizados se busca al iniciar sesión");

        var conElAnterior = await CanjearAsync(arnes, pendienteId, enviadoAlCorreoAnterior);
        conElAnterior.Succeeded.Should().BeFalse("quien recibió el enlace en el correo equivocado ya no puede usarlo");
        conElAnterior.Errors.Should().ContainSingle(e => e.Code == "InvalidToken");
        (await TieneContrasenaAsync(arnes.CadenaPropietario, pendienteId)).Should().BeFalse();

        var conElNuevo = await CanjearAsync(arnes, pendienteId, correccion.Valor);
        conElNuevo.Succeeded.Should().BeTrue(
            "control positivo: el enlace emitido al corregir sí vale. Errores: "
            + string.Join(", ", conElNuevo.Errors.Select(e => e.Code)));
    }

    [Fact]
    public async Task La_correccion_queda_auditada_con_el_correo_anterior_y_el_nuevo()
    {
        var adminId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, pendienteId, "mal-escrito@caemanager.local", contrasena: null);

        (await CorregirAsync(arnes, pendienteId, "bien-escrito@caemanager.local")).EsExitoso.Should().BeTrue();

        var fila = (await LeerEmisionesAsync(arnes.CadenaPropietario, pendienteId, RegistroAuditoria.AccionCorreoCorregido))
            .Should().ContainSingle().Subject;
        fila.TenantId.Should().Be(TenantA);
        fila.UsuarioId.Should().Be(adminId, "quien corrige es el Administrador, no la cuenta afectada");
        fila.ActorRealUsuarioId.Should().Be(adminId);
        fila.DatosAntes.Should().Contain("\"Email\":\"mal-escrito@caemanager.local\"")
            .And.NotContain("bien-escrito@caemanager.local");
        fila.DatosDespues.Should().Contain("\"Email\":\"bien-escrito@caemanager.local\"")
            .And.NotContain("mal-escrito@caemanager.local");
        fila.DatosDespues.Should().Contain("\"SecurityStamp\":\"***\"", "la fila dice que el sello cambió, nunca cuál es");
        (await LeerEmisionesAsync(arnes.CadenaPropietario, pendienteId)).Should().BeEmpty(
            "la corrección es una sola fila con su acción propia, no además una emisión suelta");
    }

    [Fact]
    public async Task Una_cuenta_ya_activada_no_cambia_de_correo_por_esta_via()
    {
        var adminId = Guid.NewGuid();
        var activadaId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, activadaId, "ya-activada@caemanager.local", "Arnes#2026Seguro");
        var selloAntes = await LeerSelloAsync(arnes.CadenaPropietario, activadaId);

        var correccion = await CorregirAsync(arnes, activadaId, "otro@caemanager.local");

        correccion.EsFallido.Should().BeTrue();
        correccion.Error.Should().Be(GenerarActivacionUsuarioCommandHandler.YaActivada);
        (await LeerCorreoAsync(arnes.CadenaPropietario, activadaId)).Email.Should().Be("ya-activada@caemanager.local");
        (await LeerSelloAsync(arnes.CadenaPropietario, activadaId)).Should().Be(selloAntes,
            "una corrección rechazada no cierra las sesiones de una cuenta en uso");
        (await LeerEmisionesAsync(arnes.CadenaPropietario, activadaId, RegistroAuditoria.AccionCorreoCorregido))
            .Should().BeEmpty();
    }

    /// <summary>
    /// El adaptador vuelve a comprobar «pendiente» sobre la instancia que escribe: el
    /// Command lo comprobó en otra lectura. Se llama al puerto directamente, que es
    /// donde vive esa segunda comprobación.
    /// </summary>
    [Fact]
    public async Task El_adaptador_no_corrige_el_correo_de_una_cuenta_que_ya_se_activo()
    {
        var adminId = Guid.NewGuid();
        var activadaId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, activadaId, "ya-activada@caemanager.local", "Arnes#2026Seguro");

        using var ambito = arnes.Servicios.CreateScope();
        var resultado = await Puerto(ambito.ServiceProvider).CorregirCorreoPendienteAsync(activadaId, "otro@caemanager.local");

        resultado.Error.Should().Be(AutoridadSobreCuentas.YaNoPendiente);
        (await LeerCorreoAsync(arnes.CadenaPropietario, activadaId)).Email.Should().Be("ya-activada@caemanager.local");
    }

    [Fact]
    public async Task Un_correo_que_ya_es_de_otra_cuenta_no_se_asigna_y_el_enlace_anterior_sigue_siendo_el_vigente()
    {
        var adminId = Guid.NewGuid();
        var pendienteId = Guid.NewGuid();
        await using var arnes = await CrearArnesAsync(ActorAuditoria.Normal(adminId), adminId);
        await CrearCuentaAsync(arnes, adminId, "admin@caemanager.local", "Arnes#2026Seguro");
        await CrearCuentaAsync(arnes, pendienteId, "mal-escrito@caemanager.local", contrasena: null);
        var selloAntes = await LeerSelloAsync(arnes.CadenaPropietario, pendienteId);

        using var circuito = arnes.Servicios.CreateScope();
        var sp = circuito.ServiceProvider;
        var correccion = await new CorregirCorreoCuentaPendienteCommandHandler(Puerto(sp), sp.GetRequiredService<ICurrentUserService>())
            .Handle(new CorregirCorreoCuentaPendienteCommand(pendienteId, "admin@caemanager.local"), default);

        correccion.EsFallido.Should().BeTrue("ese correo ya es el de otra cuenta");
        correccion.Error.Codigo.Should().Be("Usuarios.FalloAlCorregirCorreo");
        var siguienteGuardadoDelCircuito = () => sp.GetRequiredService<CaeManagerDbContext>().SaveChangesAsync();
        await siguienteGuardadoDelCircuito.Should().NotThrowAsync();
        (await LeerCorreoAsync(arnes.CadenaPropietario, pendienteId)).Email.Should().Be("mal-escrito@caemanager.local",
            "ni la corrección rechazada ni un guardado posterior del circuito cambian el correo");
        (await LeerSelloAsync(arnes.CadenaPropietario, pendienteId)).Should().Be(selloAntes);
        (await LeerEmisionesAsync(arnes.CadenaPropietario, pendienteId, RegistroAuditoria.AccionCorreoCorregido))
            .Should().BeEmpty("no hubo corrección que auditar");
    }

    // ---------- Arnés ----------

    private static Task<ArnesDeArranqueRuntime> CrearArnesAsync(ActorAuditoria actor, Guid usuarioDeSesion) =>
        ArnesDeArranqueRuntime.CrearAsync(
            datosDePruebaActivos: false,
            tenantActualPersonalizado: new TenantFijo(TenantA),
            actorAuditoriaPersonalizado: new ActorFijo(actor),
            currentUserServicePersonalizado: new UsuarioFijo(usuarioDeSesion, "Administrador", TenantA));

    private static GestionCuentasUsuarioIdentity Puerto(IServiceProvider sp)
    {
        var usuarios = sp.GetRequiredService<UserManager<ApplicationUser>>();
        var contexto = sp.GetRequiredService<CaeManagerDbContext>();
        var puerta = new PuertaAccesoDatos();
        return new GestionCuentasUsuarioIdentity(
            usuarios,
            puerta,
            new DirectorioUsuariosTenant(usuarios, contexto, sp.GetRequiredService<ITenantActual>(), puerta, contexto),
            contexto);
    }

    /// <summary>Emite por el Command, en un ámbito propio como cada pulsación de «Reenviar».</summary>
    private static async Task<string> EmitirAsync(ArnesDeArranqueRuntime arnes, Guid usuarioId)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        var resultado = await new GenerarActivacionUsuarioCommandHandler(Puerto(sp), sp.GetRequiredService<ICurrentUserService>())
            .Handle(new GenerarActivacionUsuarioCommand(usuarioId), default);
        resultado.EsExitoso.Should().BeTrue(resultado.EsFallido ? resultado.Error.Mensaje : "");
        return resultado.Valor;
    }

    private static async Task<CaeManager.Domain.Common.Result<string>> CorregirAsync(
        ArnesDeArranqueRuntime arnes, Guid usuarioId, string correoNuevo)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var sp = ambito.ServiceProvider;
        return await new CorregirCorreoCuentaPendienteCommandHandler(Puerto(sp), sp.GetRequiredService<ICurrentUserService>())
            .Handle(new CorregirCorreoCuentaPendienteCommand(usuarioId, correoNuevo), default);
    }

    private sealed record CorreoDeCuenta(string? Email, string? UserName, string? NormalizedEmail, string? NormalizedUserName);

    private static async Task<CorreoDeCuenta> LeerCorreoAsync(string cadena, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            @"SELECT ""Email"", ""UserName"", ""NormalizedEmail"", ""NormalizedUserName"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;";
        comando.Parameters.AddWithValue("u", usuarioId);
        await using var lector = await comando.ExecuteReaderAsync();
        (await lector.ReadAsync()).Should().BeTrue("premisa: la cuenta existe");
        return new CorreoDeCuenta(
            lector.IsDBNull(0) ? null : lector.GetString(0),
            lector.IsDBNull(1) ? null : lector.GetString(1),
            lector.IsDBNull(2) ? null : lector.GetString(2),
            lector.IsDBNull(3) ? null : lector.GetString(3));
    }

    /// <summary>Lo que hace <c>RestablecerContrasena.razor.cs</c> con el <c>code</c> de la URL.</summary>
    private static async Task<IdentityResult> CanjearAsync(ArnesDeArranqueRuntime arnes, Guid usuarioId, string tokenCodificado)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var um = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = await um.FindByIdAsync(usuarioId.ToString());
        return await um.ResetPasswordAsync(
            usuario!, Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(tokenCodificado)), ContrasenaNueva);
    }

    private static async Task CrearCuentaAsync(ArnesDeArranqueRuntime arnes, Guid id, string email, string? contrasena)
    {
        using var ambito = arnes.Servicios.CreateScope();
        var um = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = new ApplicationUser
        {
            Id = id,
            UserName = email,
            Email = email,
            NombreCompleto = email,
            EmailConfirmed = true,
            TenantId = TenantA,
        };

        var resultado = contrasena is null ? await um.CreateAsync(usuario) : await um.CreateAsync(usuario, contrasena);
        resultado.Succeeded.Should().BeTrue(
            "errores: " + string.Join(", ", resultado.Errors.Select(e => e.Code + ":" + e.Description)));
    }

    /// <summary>
    /// El <c>UserManager</c> del arnés más un validador que, la primera vez que valida
    /// una actualización, ejecuta <paramref name="enMitadDeLaEscritura"/>: lo que otra
    /// conexión haría entre la lectura de la cuenta y su <c>UpdateAsync</c>.
    /// </summary>
    private static UserManager<ApplicationUser> UserManagerQueSufreLaCarrera(
        IServiceProvider sp, Func<Task> enMitadDeLaEscritura) => new(
        sp.GetRequiredService<IUserStore<ApplicationUser>>(),
        sp.GetRequiredService<IOptions<IdentityOptions>>(),
        sp.GetRequiredService<IPasswordHasher<ApplicationUser>>(),
        [.. sp.GetServices<IUserValidator<ApplicationUser>>(), new ValidadorQueDejaPasarLaCarrera(enMitadDeLaEscritura)],
        sp.GetServices<IPasswordValidator<ApplicationUser>>(),
        sp.GetRequiredService<ILookupNormalizer>(),
        sp.GetRequiredService<IdentityErrorDescriber>(),
        sp,
        sp.GetRequiredService<ILogger<UserManager<ApplicationUser>>>());

    private sealed class ValidadorQueDejaPasarLaCarrera(Func<Task> enMitadDeLaEscritura) : IUserValidator<ApplicationUser>
    {
        private bool _yaOcurrio;

        public async Task<IdentityResult> ValidateAsync(UserManager<ApplicationUser> manager, ApplicationUser user)
        {
            if (!_yaOcurrio)
            {
                _yaOcurrio = true;
                await enMitadDeLaEscritura();
            }

            return IdentityResult.Success;
        }
    }

    /// <summary>Lo que deja en la fila fijar la contraseña desde el enlace: hash y sello de concurrencia nuevos.</summary>
    private static async Task ActivarPorOtraConexionAsync(string cadena, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText =
            @"UPDATE ""AspNetUsers"" SET ""PasswordHash"" = 'fijada-por-otra-conexion', ""ConcurrencyStamp"" = @c WHERE ""Id"" = @u;";
        comando.Parameters.AddWithValue("c", Guid.NewGuid().ToString());
        comando.Parameters.AddWithValue("u", usuarioId);
        (await comando.ExecuteNonQueryAsync()).Should().Be(1, "premisa: la otra conexión llega a escribir la cuenta");
    }

    // ---------- Lecturas como PROPIETARIO (sin RLS): lo que de verdad quedó escrito ----------

    private sealed record FilaDeEmision(
        Guid TenantId, string? DatosAntes, string? DatosDespues, Guid? UsuarioId, Guid? ActorRealUsuarioId, DateTime FechaUtc);

    private static async Task<List<FilaDeEmision>> LeerEmisionesAsync(
        string cadena, Guid usuarioId, string accion = RegistroAuditoria.AccionActivacionEmitida)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = @"
SELECT ""TenantId"", ""DatosAntes"", ""DatosDespues"", ""UsuarioId"", ""ActorRealUsuarioId"", ""FechaUtc""
FROM ""RegistrosAuditoria""
WHERE ""EntidadTipo"" = @entidadTipo AND ""EntidadId"" = @entidadId AND ""Accion"" = @accion
ORDER BY ""FechaUtc"";";
        comando.Parameters.AddWithValue("entidadTipo", EntidadTipoAuditoria.Usuario);
        comando.Parameters.AddWithValue("entidadId", usuarioId);
        comando.Parameters.AddWithValue("accion", accion);

        var filas = new List<FilaDeEmision>();
        await using var lector = await comando.ExecuteReaderAsync();
        while (await lector.ReadAsync())
            filas.Add(new FilaDeEmision(
                lector.GetGuid(0),
                lector.IsDBNull(1) ? null : lector.GetString(1),
                lector.IsDBNull(2) ? null : lector.GetString(2),
                lector.IsDBNull(3) ? null : lector.GetGuid(3),
                lector.IsDBNull(4) ? null : lector.GetGuid(4),
                lector.GetDateTime(5)));
        return filas;
    }

    private static async Task<T> EscalarAsync<T>(string cadena, string sql, Guid usuarioId)
    {
        await using var conexion = new NpgsqlConnection(cadena);
        await conexion.OpenAsync();
        await using var comando = conexion.CreateCommand();
        comando.CommandText = sql;
        comando.Parameters.AddWithValue("u", usuarioId);
        var valor = await comando.ExecuteScalarAsync();
        return valor is null or DBNull ? default! : (T)valor;
    }

    private static Task<string?> LeerSelloAsync(string cadena, Guid usuarioId) => EscalarAsync<string?>(cadena,
        @"SELECT ""SecurityStamp"" FROM ""AspNetUsers"" WHERE ""Id"" = @u;", usuarioId);

    private static Task<bool> TieneContrasenaAsync(string cadena, Guid usuarioId) => EscalarAsync<bool>(cadena,
        @"SELECT ""PasswordHash"" IS NOT NULL FROM ""AspNetUsers"" WHERE ""Id"" = @u;", usuarioId);

    private sealed class TenantFijo(Guid tenantId) : ITenantActual
    {
        public Guid? TenantId => AmbitoTenantExplicito.TenantIdActual ?? tenantId;
    }

    private sealed class ActorFijo(ActorAuditoria actor) : IActorAuditoria
    {
        public Task<ActorAuditoria> ObtenerAsync() => Task.FromResult(actor);
        public ActorAuditoria? ObtenerSiYaEstaResuelto() => actor;
    }

    private sealed class UsuarioFijo(Guid usuarioId, string rol, Guid tenantOrigenId) : ICurrentUserService
    {
        public Task<Guid?> ObtenerUsuarioActualIdAsync() => Task.FromResult<Guid?>(usuarioId);
        public Task<string?> ObtenerRolEfectivoAsync() => Task.FromResult<string?>(rol);
        public Task<string?> ObtenerRolOrigenAsync() => Task.FromResult<string?>(rol);
        public Task<Guid?> ObtenerTenantOrigenIdAsync() => Task.FromResult<Guid?>(tenantOrigenId);
        public Task<bool> TieneDobleFactorActivoAsync() => Task.FromResult(true);
    }
}

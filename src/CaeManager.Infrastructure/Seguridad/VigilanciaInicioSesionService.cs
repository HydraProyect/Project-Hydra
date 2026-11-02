using System.Globalization;
using System.Net;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Seguridad.InicioSesion;
using CaeManager.Domain.Common;
using CaeManager.Domain.Notificaciones;
using CaeManager.Domain.Seguridad;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.Seguridad;

/// <summary>
/// Registro de inicios de sesión, detección de los sospechosos, aviso, bloqueo
/// por país y respuesta «no he sido yo».
/// </summary>
public interface IVigilanciaInicioSesion
{
    /// <summary>
    /// ¿Permite el Tenant propietario de la cuenta entrar desde el país de esta
    /// petición? Sin política activa, siempre. Si la consulta falla, se permite
    /// (el bloqueo por país es un endurecimiento opcional, no la frontera de
    /// acceso) y se deja el error en el log.
    /// </summary>
    Task<bool> PermiteOrigenAsync(Guid usuarioId, Guid tenantId, CancellationToken ct = default);

    /// <summary>
    /// Registra el intento y, si es sospechoso, avisa. Nunca lanza: un fallo al
    /// registrar no impide ni permite el inicio de sesión, que ya decidió
    /// Identity; queda en el log como error.
    /// </summary>
    Task RegistrarAsync(
        Guid? usuarioId, Guid? tenantId, MetodoInicioSesion metodo, ResultadoInicioSesion resultado,
        SenalesInicioSesion senalesConocidas = SenalesInicioSesion.Ninguna, CancellationToken ct = default);

    /// <summary>
    /// «No he sido yo»: invalida la contraseña, cierra todas las sesiones de la
    /// cuenta, envía el enlace para crear una contraseña nueva y avisa al
    /// Administrador del Tenant. Una cuenta que solo entra con Microsoft no
    /// recibe enlace de contraseña: abrirle una credencial local sería una vía
    /// de entrada nueva; tiene que asegurar su cuenta de Microsoft. Si las
    /// sesiones no se pueden cerrar, lo demás sale igualmente, sin decir que lo
    /// estén (<see cref="ResultadoNoHeSidoYo.SesionesSinCerrar"/>).
    /// </summary>
    Task<ResultadoNoHeSidoYo> ConfirmarNoHeSidoYoAsync(string usuarioId, string codigo, CancellationToken ct = default);
}

/// <summary>Desenlace de «no he sido yo», para que la página no prometa un correo que no salió.</summary>
public enum ResultadoNoHeSidoYo
{
    /// <summary>El enlace no es válido, caducó o ya se usó. No se ha cambiado nada.</summary>
    EnlaceNoValido = 0,

    /// <summary>Sesiones cerradas, contraseña invalidada y enlace de contraseña nueva enviado.</summary>
    Protegida = 1,

    /// <summary>Sesiones cerradas y contraseña invalidada, pero el correo con el enlace no salió.</summary>
    ProtegidaSinCorreo = 2,

    /// <summary>Cuenta sin contraseña local (solo Microsoft): sesiones cerradas, sin enlace de contraseña.</summary>
    ProtegidaCuentaMicrosoft = 3,

    /// <summary>
    /// La contraseña, si había, ya no vale, pero las sesiones no se pudieron cerrar. La señal, el enlace
    /// y el aviso al Administrador salieron igualmente; la página no dice que la cuenta esté protegida.
    /// </summary>
    SesionesSinCerrar = 4,
}

/// <summary>
/// Implementación de <see cref="IVigilanciaInicioSesion"/>.
///
/// <para>
/// <b>Tenant.</b> En el inicio de sesión todavía no hay sesión ni Tenant en el
/// contexto. Todo lo que lee o escribe filas va en un ámbito de servicios propio
/// y dentro de <see cref="AmbitoTenantExplicito"/> con el Tenant de la propia
/// cuenta, que Identity acaba de resolver (mismo criterio que
/// <c>AlmacenUsuarios</c>): la fila nace con RLS estándar del Tenant
/// propietario de la cuenta y el WITH CHECK de la política lo comprueba.
/// </para>
///
/// <para>
/// <b>Planos.</b> Si la petición es de un Actor de Plataforma TALVEG en Sesión
/// Privilegiada que actúa como el Usuario simulado, la fila guarda los dos por
/// separado y no avisa a la persona simulada: el acceso de soporte se audita en
/// su propio registro, no como un inicio de sesión sospechoso de ella.
/// </para>
/// </summary>
public sealed class VigilanciaInicioSesionService(
    IHttpContextAccessor accesoHttp,
    IServiceScopeFactory ambitos,
    IGeolocalizadorIp geolocalizador,
    RafagaFallosPorIp rafagas,
    IActorAuditoria actorAuditoria,
    IOptions<OpcionesInicioSesionSeguro> opciones,
    ILogger<VigilanciaInicioSesionService> logger,
    CaeManager.Application.Seguridad.Incidentes.ISenalesIncidenteSeguridad senalesIncidente) : IVigilanciaInicioSesion
{
    /// <summary>Cookie con el identificador aleatorio del navegador (estrictamente necesaria, de seguridad).</summary>
    public const string CookieDispositivo = "talveg_dispositivo";

    public async Task<bool> PermiteOrigenAsync(Guid usuarioId, Guid tenantId, CancellationToken ct = default)
    {
        try
        {
            PoliticaPaisesInicioSesion? politica;
            using (var ambito = ambitos.CreateScope())
            using (AmbitoTenantExplicito.Establecer(tenantId))
            {
                var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
                politica = await db.PoliticasPaisesInicioSesion.AsNoTracking().FirstOrDefaultAsync(ct);
            }

            if (politica is null || !politica.Activa)
                return true;

            var permite = politica.Permite(Origen().Ubicacion?.PaisIso);
            if (!permite)
                logger.LogWarning("Inicio de sesión rechazado por país no permitido para la cuenta {UsuarioId}.", usuarioId);
            return permite;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo evaluar el bloqueo por país de la cuenta {UsuarioId}; se permite la entrada.", usuarioId);
            return true;
        }
    }

    public async Task RegistrarAsync(
        Guid? usuarioId, Guid? tenantId, MetodoInicioSesion metodo, ResultadoInicioSesion resultado,
        SenalesInicioSesion senalesConocidas = SenalesInicioSesion.Ninguna, CancellationToken ct = default)
    {
        try
        {
            var ahora = DateTime.UtcNow;
            var (ip, ubicacion) = Origen();
            var huella = HuellaDispositivo();
            var dispositivo = DispositivoDeInicioSesion.Describir(accesoHttp.HttpContext?.Request.Headers.UserAgent.ToString());
            var senales = senalesConocidas;

            // El fallo contra una cuenta inexistente cuenta para la ráfaga, pero
            // no la da por avisada: no hay Administrador del Tenant al que avisar,
            // y el aviso tiene que salir con el primer intento que dé con una
            // cuenta real dentro de la ventana.
            var hayAQuienAvisar = usuarioId is not null && tenantId is not null;
            if (resultado is ResultadoInicioSesion.Fallido or ResultadoInicioSesion.Bloqueado
                && ip is not null && rafagas.RegistrarFallo(ip.ToString(), ahora, hayAQuienAvisar))
            {
                senales |= SenalesInicioSesion.RafagaFallos;
                logger.LogWarning("Ráfaga de inicios de sesión fallidos desde una misma IP (último intento contra la cuenta {UsuarioId}).",
                    usuarioId?.ToString() ?? "desconocida");
            }

            // Cuenta inexistente: no hay Tenant propietario ni persona a la que
            // avisar. Solo cuenta para la ráfaga, arriba.
            if (usuarioId is null || tenantId is null)
                return;

            var actor = actorAuditoria.ObtenerSiYaEstaResuelto();
            var actorReal = actor is { UsuarioSimuladoId: { } simulado } && simulado == usuarioId ? actor.Value.ActorRealUsuarioId : null;

            using var ambito = ambitos.CreateScope();
            using var _ = AmbitoTenantExplicito.Establecer(tenantId.Value);
            var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();

            if (resultado == ResultadoInicioSesion.Correcto && actorReal is null)
            {
                var desde = ahora.AddMonths(-RegistroInicioSesion.MesesRetencion);
                var historial = await db.RegistrosInicioSesion.AsNoTracking()
                    .Where(r => r.UsuarioId == usuarioId && r.Resultado == ResultadoInicioSesion.Correcto
                                && r.ActorRealUsuarioId == null && r.OcurridoEnUtc >= desde)
                    .OrderByDescending(r => r.OcurridoEnUtc)
                    .Take(500)
                    .Select(r => new InicioSesionPrevio(r.OcurridoEnUtc, r.PaisIso, r.Latitud, r.Longitud, r.HuellaDispositivo))
                    .ToListAsync(ct);

                senales |= DetectorInicioSesionSospechoso.Evaluar(
                    new InicioSesionPrevio(ahora, ubicacion?.PaisIso, ubicacion?.Latitud, ubicacion?.Longitud, huella),
                    historial, opciones.Value.Deteccion());
            }

            var registro = new RegistroInicioSesion(
                usuarioId.Value, actorReal, ahora, metodo, resultado, ip?.ToString(),
                ubicacion?.PaisIso, ubicacion?.Ciudad, ubicacion?.Latitud, ubicacion?.Longitud,
                huella, dispositivo, senales);
            db.RegistrosInicioSesion.Add(registro);
            await db.SaveChangesAsync(ct);

            if (actorReal is not null)
                return;

            // Correcto y sospechoso: aviso a la persona (con «no he sido yo») y al
            // Administrador del Tenant. Ráfaga: solo al Administrador — la
            // contraseña no está comprometida por fallar. Rechazado por país: el
            // bloqueo ya hizo su trabajo y el intento queda en el registro; avisar
            // a la persona por cada intento dejaría a cualquiera inundar su buzón.
            if (resultado == ResultadoInicioSesion.Correcto && registro.EsSospechoso)
                await AvisarAsync(ambito.ServiceProvider, db, registro, tenantId.Value, avisarPersona: true, ct);
            else if (senales.HasFlag(SenalesInicioSesion.RafagaFallos))
                await AvisarAsync(ambito.ServiceProvider, db, registro, tenantId.Value, avisarPersona: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo registrar el inicio de sesión de la cuenta {UsuarioId}.", usuarioId?.ToString() ?? "desconocida");
        }
    }

    public async Task<ResultadoNoHeSidoYo> ConfirmarNoHeSidoYoAsync(string usuarioId, string codigo, CancellationToken ct = default)
    {
        string token;
        try
        {
            token = Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(codigo));
        }
        catch (FormatException)
        {
            return ResultadoNoHeSidoYo.EnlaceNoValido;
        }

        // Un identificador que no es GUID haría lanzar al almacén de Identity.
        if (!Guid.TryParse(usuarioId, out _))
            return ResultadoNoHeSidoYo.EnlaceNoValido;

        ApplicationUser? usuario;
        using var ambito = ambitos.CreateScope();
        var userManager = ambito.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        using (AmbitoIdentificacionSinTenant.Abrir())
        {
            usuario = await userManager.FindByIdAsync(usuarioId);
            if (usuario is null
                || !await userManager.VerifyUserTokenAsync(usuario, ProveedorTokenNoHeSidoYo.Nombre, ProveedorTokenNoHeSidoYo.Proposito, token))
                return ResultadoNoHeSidoYo.EnlaceNoValido;
        }

        // Todo lo que escribe va con el Tenant de la propia cuenta, no con el del
        // workspace que el navegador tenga abierto si hay otra sesión iniciada.
        using var ambitoTenant = AmbitoTenantExplicito.Establecer(usuario.TenantId);

        // 1. La contraseña que se usó deja de valer: sin contraseña local no se
        //    entra con ella, y la nueva solo se crea desde el buzón de la persona.
        var teniaContrasena = await userManager.HasPasswordAsync(usuario);
        if (teniaContrasena)
        {
            var quitada = await userManager.RemovePasswordAsync(usuario);
            if (!quitada.Succeeded)
            {
                logger.LogError("«No he sido yo»: no se pudo invalidar la contraseña de la cuenta {UsuarioId}.", usuario.Id);
                return ResultadoNoHeSidoYo.EnlaceNoValido;
            }
        }

        // 2. Todas las sesiones se revocan y el sello rota, lo que también invalida este enlace.
        //    Solo con el sello, durante el intervalo de revalidación una cookie robada podía llegar a
        //    RefreshSignInAsync y salir con el sello nuevo. Va en este mismo ámbito para que el enlace del
        //    paso 3 nazca con el sello ya rotado. Un fallo no corta lo que sigue: la contraseña ya no
        //    está y este enlace ya no sirve, así que perder la señal, el enlace o el aviso dejaría la
        //    cuenta sin credencial y a nadie enterado. Se registra como error y ningún texto de después
        //    dice que las sesiones estén cerradas.
        var sesionesCerradas = true;
        try
        {
            await ambito.ServiceProvider.GetRequiredService<IRegistroSesionesUsuario>().CerrarTodasAsync(usuario.Id, ct);
            logger.LogWarning("«No he sido yo» confirmado: contraseña invalidada y sesiones cerradas para la cuenta {UsuarioId}.", usuario.Id);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            sesionesCerradas = false;
            logger.LogError(ex, "«No he sido yo» confirmado sin poder cerrar las sesiones de la cuenta {UsuarioId}: se sigue con la señal, el enlace y el aviso.", usuario.Id);
        }

        // Señal de incidente de seguridad sospechado (plano de Plataforma): la persona titular
        // dice que alguien entró con su cuenta. No lanza ni espera a la alerta.
        await senalesIncidente.InicioSesionNoReconocidoAsync(usuario.Id, usuario.TenantId, teniaContrasena, ct);

        // 3. Enlace para crear la contraseña nueva, al mismo buzón. Solo si la
        //    cuenta tenía contraseña: a una cuenta que entra con Microsoft no se
        //    le abre una credencial local nueva.
        var resultado = ResultadoNoHeSidoYo.ProtegidaCuentaMicrosoft;
        if (teniaContrasena)
        {
            resultado = ResultadoNoHeSidoYo.ProtegidaSinCorreo;
            if (!string.IsNullOrWhiteSpace(usuario.Email) && UrlBase() is { } urlBase)
            {
                var reset = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(await userManager.GeneratePasswordResetTokenAsync(usuario)));
                var enlace = $"{urlBase}/cuenta/restablecer-contrasena?userId={usuario.Id}&code={reset}";
                var estadoCuenta = sesionesCerradas
                    ? "Hemos cerrado todas tus sesiones abiertas y tu contraseña anterior ya no sirve."
                    : "Tu contraseña anterior ya no sirve, pero no hemos podido cerrar todas tus sesiones abiertas: se cerrarán al crear la contraseña nueva.";
                var asunto = sesionesCerradas ? "Tu cuenta está protegida" : "Crea una contraseña nueva cuanto antes";
                var cuerpo = $"""
                    <p>Has indicado que un inicio de sesión en tu cuenta de {Marca.Nombre} no fuiste tú.</p>
                    <p>{estadoCuenta}</p>
                    <p><a href="{WebUtility.HtmlEncode(enlace)}">Crear una contraseña nueva</a></p>
                    <p>Este enlace caduca en 60 minutos. Si caduca, pide otro desde «¿Olvidaste tu contraseña?» en la pantalla de inicio de sesión.</p>
                    """;
                var email = ambito.ServiceProvider.GetRequiredService<IEmailService>();
                var envio = await email.EnviarAsync(usuario.Email, $"{asunto} — {Marca.Nombre}", cuerpo, TipoAvisoCorreo.Seguridad);
                if (envio.EsFallido)
                    logger.LogWarning("No se pudo enviar el enlace de contraseña nueva tras «no he sido yo» a {UsuarioId}.", usuario.Id);
                else
                    resultado = ResultadoNoHeSidoYo.Protegida;
            }
        }

        // 4. Aviso al Administrador del Tenant.
        try
        {
            var db = ambito.ServiceProvider.GetRequiredService<CaeManagerDbContext>();
            var administradores = await AdministradoresAsync(db, userManager.NormalizeName(Roles.Administrador), usuario.TenantId, null, ct);
            var sesiones = sesionesCerradas ? "Se han cerrado sus sesiones" : "No se han podido cerrar sus sesiones";
            var texto = teniaContrasena
                ? $"{usuario.NombreCompleto} ha indicado que un inicio de sesión en su cuenta no fue suyo. {sesiones} y debe crear una contraseña nueva."
                : $"{usuario.NombreCompleto} ha indicado que un inicio de sesión en su cuenta no fue suyo. {sesiones}; entra con Microsoft, así que tiene que asegurar su cuenta de Microsoft.";
            var titulo = sesionesCerradas ? "Cuenta protegida por «no he sido yo»" : "«No he sido yo» con sesiones sin cerrar";
            await NotificarAdministradoresAsync(ambito.ServiceProvider, db, administradores,
                titulo, texto, $"<p>{WebUtility.HtmlEncode(texto)}</p>", ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "No se pudo avisar al Administrador del Tenant del «no he sido yo» de la cuenta {UsuarioId}.", usuario.Id);
        }

        return sesionesCerradas ? resultado : ResultadoNoHeSidoYo.SesionesSinCerrar;
    }

    private async Task AvisarAsync(
        IServiceProvider servicios, CaeManagerDbContext db, RegistroInicioSesion registro, Guid tenantId, bool avisarPersona, CancellationToken ct)
    {
        var userManager = servicios.GetRequiredService<UserManager<ApplicationUser>>();
        var usuario = await userManager.FindByIdAsync(registro.UsuarioId.ToString());
        if (usuario is null || usuario.TenantId != tenantId)
            return;

        var momento = registro.OcurridoEnUtc.EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm", CultureInfo.InvariantCulture);
        var lugar = Lugar(registro);
        var motivos = Motivos(registro.Senales);
        var detalle = $"""
            <ul>
              <li>Fecha y hora (Madrid): {momento}</li>
              <li>Ubicación aproximada: {WebUtility.HtmlEncode(lugar)}</li>
              <li>Dispositivo: {WebUtility.HtmlEncode(registro.Dispositivo ?? "desconocido")}</li>
              <li>Motivo del aviso: {WebUtility.HtmlEncode(motivos)}</li>
            </ul>
            <p style="font-size:12px;color:#666">Ubicación aproximada según la <a href="https://db-ip.com">geolocalización IP de DB-IP</a> (CC BY 4.0).</p>
            """;

        var email = servicios.GetRequiredService<IEmailService>();
        if (avisarPersona && !string.IsNullOrWhiteSpace(usuario.Email) && usuario.EmailConfirmed && UrlBase() is { } urlBase)
        {
            var token = await userManager.GenerateUserTokenAsync(usuario, ProveedorTokenNoHeSidoYo.Nombre, ProveedorTokenNoHeSidoYo.Proposito);
            var enlace = $"{urlBase}/cuenta/no-he-sido-yo?userId={usuario.Id}&code={WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token))}";
            var cuerpo = $"""
                <p>Hemos detectado un inicio de sesión inusual en tu cuenta de {Marca.Nombre}.</p>
                {detalle}
                <p>Si fuiste tú, no tienes que hacer nada.</p>
                <p>Si no fuiste tú: <a href="{WebUtility.HtmlEncode(enlace)}">No he sido yo</a>. Cerraremos todas tus sesiones y tendrás que crear una contraseña nueva. El enlace caduca en {opciones.Value.DiasVigenciaEnlaceNoHeSidoYo} días.</p>
                """;
            var envio = await email.EnviarAsync(usuario.Email, $"Inicio de sesión inusual en tu cuenta — {Marca.Nombre}", cuerpo, TipoAvisoCorreo.Seguridad);
            if (envio.EsFallido)
                logger.LogWarning("No se pudo enviar el aviso de inicio de sesión inusual a la cuenta {UsuarioId}.", usuario.Id);
        }

        var administradores = await AdministradoresAsync(db, userManager.NormalizeName(Roles.Administrador), usuario.TenantId, usuario.Id, ct);
        var titulo = avisarPersona ? "Inicio de sesión inusual" : "Ráfaga de intentos fallidos";
        var texto = $"{usuario.NombreCompleto}: {motivos}. {lugar}, {momento}.";
        await NotificarAdministradoresAsync(servicios, db, administradores, titulo,
            texto, $"<p>Aviso de seguridad sobre la cuenta de {WebUtility.HtmlEncode(usuario.NombreCompleto)} en {Marca.Nombre}.</p>{detalle}", ct);
    }

    /// <summary>Lo que hace falta para avisar a un Administrador, sin las credenciales de su cuenta (ASVS 4.0.3 V4.1.3).</summary>
    internal sealed record DestinatarioAdministrador(Guid Id, string? Email, DateTimeOffset? LockoutEnd);

    /// <summary>
    /// Los Administradores activos del Tenant propietario de la cuenta avisada, salvo <paramref name="usuarioId"/>
    /// si se indica. El rol no filtra por Tenant y la política de <c>AspNetUsers</c> deja ver también a quien opera
    /// este Tenant desde otro: el aviso es solo para el Administrador del Tenant propietario.
    /// </summary>
    internal static async Task<List<DestinatarioAdministrador>> AdministradoresAsync(
        CaeManagerDbContext db, string rolNormalizado, Guid tenantId, Guid? usuarioId, CancellationToken ct)
    {
        var candidatos = await (from cuenta in db.Users.AsNoTracking()
                                join asignacion in db.UserRoles on cuenta.Id equals asignacion.UserId
                                join rol in db.Roles on asignacion.RoleId equals rol.Id
                                where cuenta.TenantId == tenantId && cuenta.Id != usuarioId && rol.NormalizedName == rolNormalizado
                                select new DestinatarioAdministrador(cuenta.Id, cuenta.Email, cuenta.LockoutEnd)).ToListAsync(ct);
        var ahora = DateTimeOffset.UtcNow;
        return candidatos.Where(a => !ApplicationUser.BloqueoDesactivaLaCuenta(a.LockoutEnd, ahora)).ToList();
    }

    private async Task NotificarAdministradoresAsync(
        IServiceProvider servicios, CaeManagerDbContext db, IReadOnlyList<DestinatarioAdministrador> administradores,
        string titulo, string texto, string cuerpoHtml, CancellationToken ct)
    {
        foreach (var administrador in administradores)
            db.NotificacionesUsuario.Add(new NotificacionUsuario(administrador.Id, titulo, texto));
        if (administradores.Count > 0)
            await db.SaveChangesAsync(ct);

        var email = servicios.GetRequiredService<IEmailService>();
        foreach (var administrador in administradores)
        {
            if (string.IsNullOrWhiteSpace(administrador.Email))
                continue;
            var envio = await email.EnviarAsync(administrador.Email, $"{titulo} — {Marca.Nombre}", cuerpoHtml, TipoAvisoCorreo.Seguridad);
            if (envio.EsFallido)
                logger.LogWarning("No se pudo enviar un aviso de seguridad al Administrador {UsuarioId}.", administrador.Id);
        }
    }

    private (IPAddress? Ip, UbicacionIp? Ubicacion) Origen()
    {
        var contexto = accesoHttp.HttpContext;
        if (contexto is null)
            return (null, null);

        // Sin la captura de Program.cs no se sabe qué par habló de verdad:
        // Connection.RemoteIpAddress ya pasó por UseForwardedHeaders, que acepta
        // X-Forwarded-For de cualquiera. Mejor IP desconocida que una inventada.
        var capturado = contexto.Features.Get<OrigenConexionCapturado>();
        if (capturado is null)
            return (null, null);

        var ip = IpClienteDeConfianza.Resolver(capturado.ParConexion, capturado.XForwardedFor, opciones.Value.RedesDeConfianza());
        return (ip, geolocalizador.Ubicar(ip));
    }

    /// <summary>Huella del navegador; emite la cookie de dispositivo si falta o no es válida.</summary>
    private string? HuellaDispositivo()
    {
        var contexto = accesoHttp.HttpContext;
        if (contexto is null)
            return null;

        var valor = contexto.Items[CookieDispositivo] as string ?? contexto.Request.Cookies[CookieDispositivo];
        if (!DispositivoDeInicioSesion.EsIdentificadorValido(valor))
        {
            if (contexto.Response.HasStarted)
                return null;
            valor = DispositivoDeInicioSesion.NuevoIdentificador();
            contexto.Response.Cookies.Append(CookieDispositivo, valor, new CookieOptions
            {
                HttpOnly = true,
                Secure = contexto.Request.IsHttps,
                SameSite = SameSiteMode.Lax,
                IsEssential = true,
                Path = "/",
                MaxAge = TimeSpan.FromDays(400),
            });
            contexto.Items[CookieDispositivo] = valor;
        }

        return DispositivoDeInicioSesion.Huella(valor!);
    }

    private string? UrlBase()
    {
        var peticion = accesoHttp.HttpContext?.Request;
        return peticion is null || !peticion.Host.HasValue ? null : $"{peticion.Scheme}://{peticion.Host}{peticion.PathBase}";
    }

    private static string Lugar(RegistroInicioSesion registro)
    {
        if (registro.PaisIso is null)
            return "ubicación desconocida";
        var pais = NombrePais(registro.PaisIso);
        return registro.Ciudad is null ? pais : $"{registro.Ciudad} ({pais})";
    }

    internal static string Motivos(SenalesInicioSesion senales)
    {
        var motivos = new List<string>();
        if (senales.HasFlag(SenalesInicioSesion.PaisNuevo)) motivos.Add("país nuevo para la cuenta");
        if (senales.HasFlag(SenalesInicioSesion.DispositivoNuevo)) motivos.Add("dispositivo nuevo");
        if (senales.HasFlag(SenalesInicioSesion.ViajeImposible)) motivos.Add("viaje imposible desde el inicio anterior");
        if (senales.HasFlag(SenalesInicioSesion.RafagaFallos)) motivos.Add("ráfaga de intentos fallidos desde la misma IP");
        if (senales.HasFlag(SenalesInicioSesion.PaisNoPermitido)) motivos.Add("país no permitido");
        return motivos.Count == 0 ? "sin motivo" : string.Join(", ", motivos);
    }

    private static string NombrePais(string iso)
    {
        var anterior = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es-ES");
            return $"{new RegionInfo(iso).DisplayName} ({iso})";
        }
        catch (ArgumentException)
        {
            return iso;
        }
        finally
        {
            CultureInfo.CurrentUICulture = anterior;
        }
    }
}

/// <summary>Vigencia propia del enlace «no he sido yo» (la de restablecer contraseña es de 60 minutos).</summary>
public sealed class OpcionesTokenNoHeSidoYo : DataProtectionTokenProviderOptions
{
    public OpcionesTokenNoHeSidoYo()
    {
        Name = ProveedorTokenNoHeSidoYo.Nombre;
        TokenLifespan = TimeSpan.FromDays(7);
    }
}

/// <summary>
/// Proveedor del token del enlace «no he sido yo». Va ligado al sello de
/// seguridad de la cuenta: al confirmarlo se rota el sello, así que el enlace
/// solo sirve una vez.
/// </summary>
public sealed class ProveedorTokenNoHeSidoYo(
    Microsoft.AspNetCore.DataProtection.IDataProtectionProvider proteccion,
    IOptions<OpcionesTokenNoHeSidoYo> opciones,
    ILogger<DataProtectorTokenProvider<ApplicationUser>> logger)
    : DataProtectorTokenProvider<ApplicationUser>(proteccion, opciones, logger)
{
    public const string Nombre = "NoHeSidoYo";
    public const string Proposito = "NoHeSidoYo";
}

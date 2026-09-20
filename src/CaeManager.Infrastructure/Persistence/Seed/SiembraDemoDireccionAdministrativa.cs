using CaeManager.Application.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CaeManager.Infrastructure.Persistence.Seed;

/// <summary>
/// El camino de la demo a dirección hacia un entorno real (producción): la misma
/// matriz que <see cref="EscenariosDireccionDemoSeeder"/> siembra en local
/// (<see cref="EscenariosDireccionDemoSeeder.SembrarRamaAsync"/>), pero como una
/// operación administrativa explícita, auditable y reversible, no como una
/// siembra de arranque.
///
/// <para>
/// <b>Qué la separa de la siembra local.</b> (1) No usa <c>DatosPrueba:Activo</c>
/// ni <see cref="CredencialesDemo"/>: cada cuenta lleva una contraseña aleatoria
/// distinta que solo va a un fichero en memoria (ver
/// <see cref="ValidarDirectorioDeSalida"/>), nunca a un log ni a la salida
/// estándar. (2) Las direcciones son de un dominio que controla el propietario,
/// nunca <c>@caemanager.local</c> (esas 35 cuentas siguen bloqueadas por el
/// incidente de 2026-08-27 y esta operación no las toca). (3) Crea un lote
/// propio de siete Tenants con nombre limpio, distinto de los heredados de
/// <see cref="DelegacionDemoSeeder"/>, que quedan intactos. (4) Marca cada Tenant
/// que crea (<c>DatosDemoCompletadosEnUtc</c>) y la retirada lo exige además del
/// nombre. (5) Solo es alcanzable desde el modo de CLI
/// <see cref="ArgumentoSembrar"/> de <c>Program.cs</c>: nunca corre en el
/// arranque normal (ver <c>SiembraDemoDireccionSoloDesdeElModoCliTests</c>).
/// </para>
///
/// <para>
/// <b>Falla cerrada, antes de escribir.</b> <see cref="ValidarPrecondiciones"/> y
/// <see cref="VerificarNombresLibresAsync"/> corren antes de que exista una sola
/// fila: si un Tenant del lote ya existe SIN el marcador (un Tenant real que se
/// llame igual), la operación se niega en vez de sembrar demo dentro de él.
/// </para>
/// </summary>
public static partial class SiembraDemoDireccionAdministrativa
{
    public const string ArgumentoSembrar = "--sembrar-demo-direccion";
    public const string ArgumentoRetirar = "--retirar-demo-direccion";

    public const string ClaveDominioCorreo = "DemoDireccion:DominioCorreo";
    public const string ClaveDirectorioCredenciales = "DemoDireccion:DirectorioCredenciales";
    public const string ClaveConfirmarEntorno = "DemoDireccion:ConfirmarEntorno";

    public const string NombreTenantOperador = "ArcoSPA Prevención S.L.";

    private const int LongitudContrasena = 24;

    /// <summary>
    /// Nombre de cada Tenant en el lote real. Los cuatro heredados de la demo
    /// (con su sufijo «… demo») se sirven con nombre limpio, que es el que la
    /// dirección ve en pantalla. Duff y Pizza Planet llevan además un nombre PROPIO del
    /// lote: con el de la demo local, una siembra local previa (que también marca sus
    /// Tenants) las dejaría pasar la guarda de nombres y esta siembra las reutilizaría
    /// —y <c>--retirar-demo-direccion</c> las borraría— (hallazgo de la revisión Codex).
    /// </summary>
    private static readonly Dictionary<string, string> NombreLimpio = new()
    {
        [DelegacionDemoSeeder.NombreTenantRefrielectric] = "Refrielectric S.L.",
        [DelegacionDemoSeeder.NombreTenantClienteDemo] = "Laboratorios Dexter S.L.",
        [DelegacionDemoSeeder.NombreTenantClienteDemo2] = "Transportes Planet Express S.A.",
        [DelegacionDemoSeeder.NombreTenantClienteDemo3] = "Hostelería Krusty Krab S.L.",
        [CatalogoEscenariosDireccionDemo.NombreTenantDuff] = "Cervezas Duff Ibérica S.A.",
        [CatalogoEscenariosDireccionDemo.NombreTenantPizzaPlanet] = "Pizza Planet Restauración S.L.",
    };

    /// <summary>Las seis ramas del lote real: el mismo catálogo que la demo local, con los nombres del lote.</summary>
    public static IReadOnlyList<RamaEscenariosDemo> Ramas { get; } =
        CatalogoEscenariosDireccionDemo.Ramas
            .Select(r => r with { NombreTenant = NombreLimpio.GetValueOrDefault(r.NombreTenant, r.NombreTenant) })
            .ToList();

    /// <summary>
    /// Los siete Tenants del lote, con el del Operador CAE al final: es el
    /// orden de retirada (las ramas dependen de su operación externa).
    /// </summary>
    public static IReadOnlyList<string> NombresTenantsDelLote { get; } =
        [.. Ramas.Select(r => r.NombreTenant), NombreTenantOperador];

    public sealed record Opciones(string DominioCorreo, string DirectorioCredenciales, string EntornoConfirmado);

    /// <param name="ContrasenaEntregada">True solo si esta ejecución creó la cuenta (y por tanto su contraseña está en el fichero); false si ya existía y no se toca.</param>
    public sealed record CuentaSembrada(string Email, string Rol, Guid Id, bool ContrasenaEntregada);

    /// <summary>Lo que el operador puede registrar: ningún dato secreto.</summary>
    public sealed record Resultado(
        IReadOnlyList<(string Nombre, Guid Id)> Tenants, IReadOnlyList<CuentaSembrada> Cuentas, string FicheroCredenciales);

    public static Opciones LeerOpciones(IConfiguration configuration) => new(
        configuration[ClaveDominioCorreo] ?? string.Empty,
        configuration[ClaveDirectorioCredenciales] ?? string.Empty,
        configuration[ClaveConfirmarEntorno] ?? string.Empty);

    internal static EscenariosDireccionDemoSeeder.EmailsEquipo EmailsDe(string dominio) => new(
        $"demo.coordinador@{dominio}", $"demo.gestor1@{dominio}", $"demo.gestor2@{dominio}");

    public static string EmailAdministrador(string dominio) => $"demo.administrador@{dominio}";

    // ── Precondiciones (antes de escribir nada) ─────────────────────────────

    /// <summary>
    /// Todo lo que se puede comprobar sin tocar la base de datos. Lanza
    /// <see cref="InvalidOperationException"/> con el motivo exacto.
    /// </summary>
    /// <param name="informacionDeMontajes">Contenido de <c>/proc/self/mountinfo</c> (parámetro para poder probarlo).</param>
    internal static void ValidarPrecondiciones(
        IConfiguration configuration, IHostEnvironment entorno, Opciones opciones,
        string? informacionDeMontajes, bool esLinux, UnixFileMode? modoDelDirectorio)
    {
        if (!string.Equals(opciones.EntornoConfirmado, entorno.EnvironmentName, StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"{ClaveConfirmarEntorno} debe ser exactamente el entorno en el que se ejecuta ('{entorno.EnvironmentName}'), " +
                $"y es '{opciones.EntornoConfirmado}': se pide para que el operador diga a mano contra qué base de datos va a escribir.");

        if (configuration.GetValue<bool>("DatosPrueba:Activo") || configuration.GetValue<bool>(EscenariosDireccionDemoSeeder.ClaveConfiguracion))
            throw new InvalidOperationException(
                "Con DatosPrueba:Activo o DatosPrueba:EscenariosDireccion activos la siembra local escribiría con la contraseña " +
                "compartida de la demo (incidente de 2026-08-27). Esta operación no se mezcla con ella: desactívalos.");

        ValidarDominio(opciones.DominioCorreo);
        ValidarDirectorioDeSalida(opciones.DirectorioCredenciales, informacionDeMontajes, esLinux, modoDelDirectorio);
    }

    private static readonly string[] SufijosDeDominioNoControlables =
        [".local", ".localhost", ".test", ".invalid", ".example", ".internal", ".lan"];

    [GeneratedRegex(@"^(?=.{4,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$")]
    private static partial Regex ExpresionDeDominio();

    /// <summary>
    /// Un dominio de verdad, en minúsculas y controlado por quien lo aporta:
    /// nunca uno reservado o local (<c>caemanager.local</c> incluido), donde
    /// cualquiera podría registrar esa dirección.
    /// </summary>
    internal static void ValidarDominio(string dominio)
    {
        if (string.IsNullOrWhiteSpace(dominio) || !ExpresionDeDominio().IsMatch(dominio))
            throw new InvalidOperationException(
                $"{ClaveDominioCorreo} debe ser un dominio en minúsculas del tipo 'ejemplo.es' (recibido: '{dominio}'). " +
                "Es el dominio de las cuentas de demo y debe controlarlo el propietario.");

        if (SufijosDeDominioNoControlables.Any(s => dominio.EndsWith(s, StringComparison.Ordinal)))
            throw new InvalidOperationException(
                $"{ClaveDominioCorreo} = '{dominio}' es un dominio local o reservado: las cuentas de demo tienen que estar en un " +
                "dominio que controle el propietario.");
    }

    /// <summary>
    /// El fichero de credenciales tiene que estar en un sitio que <b>no se
    /// respalda ni sobrevive a un reinicio</b>: un directorio de un sistema de
    /// ficheros en memoria (<c>tmpfs</c>/<c>ramfs</c>), y solo del usuario que
    /// ejecuta el proceso. El código no sabe qué rutas incluye el respaldo del
    /// servidor, así que no lo adivina: exige el único tipo de almacenamiento
    /// que ningún respaldo de ficheros recorre por defecto y que desaparece con
    /// el reinicio, y obliga a pasar la ruta a mano. Fuera de Linux se rechaza.
    /// </summary>
    internal static void ValidarDirectorioDeSalida(
        string directorio, string? informacionDeMontajes, bool esLinux, UnixFileMode? modo)
    {
        if (string.IsNullOrWhiteSpace(directorio) || !Path.IsPathRooted(directorio))
            throw new InvalidOperationException(
                $"{ClaveDirectorioCredenciales} debe ser una ruta absoluta pasada a mano (recibido: '{directorio}').");

        if (!esLinux)
            throw new InvalidOperationException(
                "El fichero de credenciales solo se escribe en Linux, en un directorio sobre tmpfs: en este sistema no se puede " +
                "comprobar que la ruta no esté respaldada.");

        var tipo = TipoDeSistemaDeFicheros(directorio, informacionDeMontajes ?? string.Empty);
        if (tipo is not ("tmpfs" or "ramfs"))
            throw new InvalidOperationException(
                $"'{directorio}' está en un sistema de ficheros '{tipo ?? "desconocido"}': el fichero de credenciales solo puede ir a " +
                "memoria (tmpfs/ramfs, p. ej. un subdirectorio de /dev/shm) para que no llegue a ningún respaldo ni a un volumen persistente.");

        const UnixFileMode ajenos =
            UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if (modo is null || (modo.Value & ajenos) != 0)
            throw new InvalidOperationException(
                $"'{directorio}' debe ser accesible solo por su propietario (modo 0700); su modo es " +
                $"{(modo is null ? "ilegible" : Convert.ToString((int)modo.Value, 8))}.");
    }

    /// <summary>
    /// Tipo de sistema de ficheros del montaje más específico que contiene la
    /// ruta, leído de <c>mountinfo</c>: <c>id padre major:minor raíz punto opciones [campos] - tipo fuente opciones</c>.
    /// </summary>
    internal static string? TipoDeSistemaDeFicheros(string ruta, string informacionDeMontajes)
    {
        var rutaNormalizada = ruta.TrimEnd('/');
        string? mejorPunto = null, mejorTipo = null;

        foreach (var linea in informacionDeMontajes.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var separador = linea.IndexOf(" - ", StringComparison.Ordinal);
            if (separador < 0) continue;

            var campos = linea[..separador].Split(' ');
            var posteriores = linea[(separador + 3)..].Split(' ');
            if (campos.Length < 5 || posteriores.Length < 1) continue;

            var punto = campos[4].Replace("\\040", " ").TrimEnd('/');
            var contiene = punto.Length == 0 || rutaNormalizada == punto || rutaNormalizada.StartsWith(punto + "/", StringComparison.Ordinal);
            if (contiene && (mejorPunto is null || punto.Length >= mejorPunto.Length))
            {
                mejorPunto = punto;
                mejorTipo = posteriores[0];
            }
        }

        return mejorTipo;
    }

    // ── Escritura ───────────────────────────────────────────────────────────

    /// <summary>
    /// Ningún Tenant del lote puede existir sin el marcador de demo: uno así
    /// sería un Tenant real con el mismo nombre, y
    /// <see cref="DelegacionDemoSeeder.AprovisionarTenantAsync"/> lo devolvería
    /// por nombre y sembraría demo dentro de él. Se niega antes de escribir.
    /// </summary>
    internal static async Task<IReadOnlyDictionary<string, Guid>> VerificarNombresLibresAsync(
        CaeManagerDbContext dbContext, CancellationToken cancellationToken)
    {
        var existentes = await dbContext.Tenants.IgnoreQueryFilters()
            .Where(t => NombresTenantsDelLote.Contains(t.Nombre))
            .Select(t => new { t.Id, t.Nombre, t.DatosDemoCompletadosEnUtc })
            .ToListAsync(cancellationToken);

        var sinMarcador = existentes.Where(t => t.DatosDemoCompletadosEnUtc is null).Select(t => t.Nombre).ToList();
        if (sinMarcador.Count > 0)
            throw new InvalidOperationException(
                $"Ya existe un Tenant con el nombre de un Tenant del lote SIN el marcador de datos de demo: {string.Join(", ", sinMarcador)}. " +
                "Podría ser un Tenant real: no se siembra nada en él.");

        return existentes.ToDictionary(t => t.Nombre, t => t.Id);
    }

    /// <summary>
    /// Una cuenta del lote que ya existe solo se reutiliza si es del Tenant del Operador CAE
    /// DEL LOTE (una re-ejecución). Si el correo pertenece a otro Tenant —o el Operador CAE
    /// del lote aún no existe—, <c>CrearUsuarioConsultoraAsync</c> la devolvería sin
    /// mirar su Tenant y la siembra la haría Administradora o Gestora CAE del lote y
    /// cambiaría su cartera. Se niega antes de escribir (hallazgo de la revisión Codex).
    /// Devuelve los correos de las cuentas que hay que CREAR.
    /// </summary>
    internal static async Task<HashSet<string>> VerificarCuentasReutilizablesAsync(
        UserManager<ApplicationUser> userManager, IEnumerable<string> emails,
        IReadOnlyDictionary<string, Guid> tenantsExistentes, CancellationToken cancellationToken)
    {
        var nuevas = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ajenas = new List<string>();
        tenantsExistentes.TryGetValue(NombreTenantOperador, out var operadorExistente);

        foreach (var email in emails)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var existente = await userManager.FindByEmailAsync(email);
            if (existente is null) nuevas.Add(email);
            else if (operadorExistente == Guid.Empty || existente.TenantId != operadorExistente) ajenas.Add(email);
        }

        if (ajenas.Count > 0)
            throw new InvalidOperationException(
                $"Ya existen cuentas con el correo de la demo que NO pertenecen al Operador CAE del lote: {string.Join(", ", ajenas)}. " +
                "Podrían ser cuentas reales: no se siembra nada.");

        return nuevas;
    }

    public static async Task<Resultado> SembrarAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager, IConfiguration configuration,
        IHostEnvironment entorno, Opciones opciones, ILogger logger, CancellationToken cancellationToken = default)
    {
        var esLinux = OperatingSystem.IsLinux();
        ValidarPrecondiciones(
            configuration, entorno, opciones,
            esLinux ? await File.ReadAllTextAsync("/proc/self/mountinfo", cancellationToken) : null,
            esLinux, esLinux && Directory.Exists(opciones.DirectorioCredenciales) ? File.GetUnixFileMode(opciones.DirectorioCredenciales) : null);

        return await EjecutarAsync(dbContext, userManager, entorno, opciones, logger, cancellationToken);
    }

    /// <summary>
    /// La siembra propiamente dicha, DESPUÉS de las comprobaciones de <see cref="SembrarAsync"/> (que dependen
    /// del sistema operativo y de <c>/proc</c>, y por eso no se pueden ejercitar en cualquier máquina de pruebas).
    /// </summary>
    internal static async Task<Resultado> EjecutarAsync(
        CaeManagerDbContext dbContext, UserManager<ApplicationUser> userManager,
        IHostEnvironment entorno, Opciones opciones, ILogger logger, CancellationToken cancellationToken)
    {
        var tenantsExistentes = await VerificarNombresLibresAsync(dbContext, cancellationToken);
        var dominio = opciones.DominioCorreo;
        var emails = EmailsDe(dominio);
        var correos = new[] { EmailAdministrador(dominio), emails.Coordinador, emails.GestorPrimero, emails.GestorSegundo };
        var correosNuevos = await VerificarCuentasReutilizablesAsync(userManager, correos, tenantsExistentes, cancellationToken);

        // El fichero se abre —con permisos 0600 DESDE su creación, no después— antes
        // de crear ninguna cuenta: si no se puede escribir, no se crea nada cuya
        // contraseña se perdería.
        var rutaFichero = Path.Combine(
            opciones.DirectorioCredenciales, $"credenciales-demo-direccion-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}.json");
        var opcionesDeFichero = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
        // Windows no admite modos Unix (lanza): solo se usa en máquinas de desarrollo; el entorno real es Linux
        // (ValidarPrecondiciones lo exige), donde el fichero nace ya con 0600.
        if (!OperatingSystem.IsWindows())
            opcionesDeFichero.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using var fichero = new FileStream(rutaFichero, opcionesDeFichero);

        // Una contraseña distinta por cuenta NUEVA; las que ya existen (del propio lote) no se tocan.
        var contrasenas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var email in correosNuevos)
            contrasenas[email] = GenerarContrasena();

        // Las contraseñas llegan al fichero ANTES de crear ninguna cuenta (hallazgo de la revisión Codex): si
        // algo falla después, toda cuenta que exista tiene su contraseña entregable en el fichero; lo peor
        // que puede pasar es que el fichero liste una cuenta que no llegó a crearse.
        await EscribirCredencialesAsync(
            fichero, entorno.EnvironmentName,
            [
                (EmailAdministrador(dominio), Roles.Administrador), (emails.Coordinador, Roles.CoordinadorCae),
                (emails.GestorPrimero, Roles.GestorCae), (emails.GestorSegundo, Roles.GestorCae),
            ],
            contrasenas, cancellationToken);

        CredencialesDemo CredencialDe(string email) =>
            new(contrasenas.GetValueOrDefault(email, string.Empty), string.Empty, string.Empty);

        var tenantOperadorId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
            dbContext, NombreTenantOperador, Domain.Tenants.PerfilVocabularioTenant.Consultora, logger, cancellationToken);
        await MarcarComoDemoAsync(dbContext, tenantOperadorId, cancellationToken);

        var administrador = await DelegacionDemoSeeder.CrearUsuarioConsultoraAsync(
                dbContext, userManager, CredencialDe(EmailAdministrador(dominio)), logger, tenantOperadorId,
                EmailAdministrador(dominio), "Administración del Operador CAE (demo)", Roles.Administrador, cancellationToken)
            ?? throw new InvalidOperationException($"No se pudo crear la cuenta {EmailAdministrador(dominio)}.");

        var equipo = await EscenariosDireccionDemoSeeder.SembrarEquipoAsync(
            dbContext, userManager, CredencialDe, emails, logger, tenantOperadorId, cancellationToken);

        var tenants = new List<(string Nombre, Guid Id)> { (NombreTenantOperador, tenantOperadorId) };
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);

        for (var indice = 0; indice < Ramas.Count; indice++)
        {
            var rama = Ramas[indice];
            var tenantPropietarioId = await DelegacionDemoSeeder.AprovisionarTenantAsync(
                dbContext, rama.NombreTenant, Domain.Tenants.PerfilVocabularioTenant.ClienteDirecto, logger, cancellationToken);
            await MarcarComoDemoAsync(dbContext, tenantPropietarioId, cancellationToken);

            await DelegacionDemoSeeder.CrearDelegacionAsync(
                dbContext, tenantOperadorId, tenantPropietarioId, administrador, logger, rama.NombreTenant, cancellationToken);

            await EscenariosDireccionDemoSeeder.SembrarRamaAsync(
                dbContext, rama, tenantPropietarioId, tenantOperadorId, equipo, hoy, indice, logger, cancellationToken);

            tenants.Add((rama.NombreTenant, tenantPropietarioId));
        }

        var cuentas = new List<CuentaSembrada>();
        foreach (var (email, nombreRol, usuario) in new[]
                 {
                     (EmailAdministrador(dominio), Roles.Administrador, administrador),
                     (emails.Coordinador, Roles.CoordinadorCae, equipo.Coordinador),
                     (emails.GestorPrimero, Roles.GestorCae, equipo.GestorPrimero),
                     (emails.GestorSegundo, Roles.GestorCae, equipo.GestorSegundo),
                 })
            cuentas.Add(new CuentaSembrada(email, nombreRol, usuario.Id, contrasenas.ContainsKey(email)));

        // Solo cuentas y Tenants, nunca contraseñas: es lo que se registra.
        logger.LogInformation(
            "Demo a dirección sembrada en {Entorno}: {Tenants} Tenants, {Cuentas} cuentas ({Nuevas} nuevas).",
            entorno.EnvironmentName, tenants.Count, cuentas.Count, cuentas.Count(c => c.ContrasenaEntregada));

        return new Resultado(tenants, cuentas, rutaFichero);
    }

    /// <summary>
    /// Marca el Tenant como creado por una siembra de demo, <b>al crearlo</b> y no
    /// al final: una siembra cortada a medias sigue siendo retirable. Sin marcador
    /// la retirada de un Tenant del lote se niega (ver
    /// <see cref="RetiradaTenantDemoService"/>).
    /// </summary>
    internal static async Task MarcarComoDemoAsync(CaeManagerDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        var tenant = await dbContext.Tenants.IgnoreQueryFilters().SingleAsync(t => t.Id == tenantId, cancellationToken);
        if (tenant.DatosDemoCompletadosEnUtc is not null) return;

        // El interceptor de auditoría sella contra un tenant resuelto (mismo motivo que en AprovisionarTenantAsync).
        using (AmbitoTenantExplicito.Establecer(tenantId))
        {
            tenant.MarcarDatosDemoCompletados();
            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task EscribirCredencialesAsync(
        FileStream fichero, string entorno, IReadOnlyList<(string Email, string Rol)> cuentas,
        IReadOnlyDictionary<string, string> contrasenas, CancellationToken cancellationToken)
    {
        var contenido = new
        {
            aviso = "FICHERO TEMPORAL con contraseñas en claro: entregar y destruir (shred -u) inmediatamente; no copiar, no respaldar.",
            entorno,
            generadoEnUtc = DateTime.UtcNow,
            cuentas = cuentas.Select(c => new
            {
                email = c.Email,
                rol = c.Rol,
                contrasena = contrasenas.GetValueOrDefault(c.Email),
                nota = contrasenas.ContainsKey(c.Email) ? null : "La cuenta ya existía: su contraseña no se ha tocado.",
            }),
        };

        await JsonSerializer.SerializeAsync(
            fichero, contenido, new JsonSerializerOptions { WriteIndented = true }, cancellationToken);
        await fichero.FlushAsync(cancellationToken);
    }

    private const string Mayusculas = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Minusculas = "abcdefghijkmnopqrstuvwxyz";
    private const string Digitos = "23456789";
    private const string Simbolos = "#%+-=?@_";

    /// <summary>
    /// 24 caracteres de <see cref="RandomNumberGenerator"/>, con al menos dos de cada clase (cumple de sobra la política
    /// de Identity) y sin caracteres que se confundan al copiarlos (0/O, 1/l/I).
    /// </summary>
    internal static string GenerarContrasena()
    {
        var todos = Mayusculas + Minusculas + Digitos + Simbolos;
        var caracteres = new List<char>(LongitudContrasena);
        foreach (var clase in new[] { Mayusculas, Minusculas, Digitos, Simbolos })
            for (var i = 0; i < 2; i++)
                caracteres.Add(clase[RandomNumberGenerator.GetInt32(clase.Length)]);
        while (caracteres.Count < LongitudContrasena)
            caracteres.Add(todos[RandomNumberGenerator.GetInt32(todos.Length)]);

        // Fisher-Yates con el mismo generador criptográfico.
        for (var i = caracteres.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (caracteres[i], caracteres[j]) = (caracteres[j], caracteres[i]);
        }

        return new string(caracteres.ToArray());
    }

    // ── Retirada ────────────────────────────────────────────────────────────

    /// <summary>
    /// Retira el lote completo, ramas primero y el Operador CAE al final. Valida
    /// TODOS los Tenants con identidad no privilegiada (nombre y marcador) antes
    /// de elevar, y no retira ninguno si alguno no lo supera: o se retira el
    /// lote o no se toca nada. Los que no existen se saltan (retirada repetible).
    /// </summary>
    public static async Task<IReadOnlyList<RetiradaTenantDemoService.ResultadoRetirada>> RetirarLoteAsync(
        CaeManagerDbContext dbContextNoPrivilegiado, Func<CaeManagerDbContext> crearContextoDeBootstrap,
        ILogger logger, CancellationToken cancellationToken = default)
    {
        var validados = new List<Domain.Tenants.Tenant>();
        foreach (var nombre in NombresTenantsDelLote)
        {
            var id = await dbContextNoPrivilegiado.Tenants.IgnoreQueryFilters()
                .Where(t => t.Nombre == nombre).Select(t => (Guid?)t.Id).SingleOrDefaultAsync(cancellationToken);
            if (id is null) continue;

            validados.Add(await RetiradaTenantDemoService.ValidarTenantRetirableAsync(dbContextNoPrivilegiado, id.Value, cancellationToken));
        }

        var resultados = new List<RetiradaTenantDemoService.ResultadoRetirada>();
        if (validados.Count == 0) return resultados;

        await using var dbContextRetirada = crearContextoDeBootstrap();
        foreach (var tenant in validados)
            resultados.Add(await RetiradaTenantDemoService.RetirarAsync(dbContextRetirada, tenant, logger, cancellationToken));

        return resultados;
    }
}

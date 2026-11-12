using CaeManager.Application.AltaAutonoma;
using CaeManager.Application.Common;
using CaeManager.Domain.AltaAutonoma;
using CaeManager.Domain.Common;
using CaeManager.Infrastructure.Identity;
using CaeManager.Infrastructure.Persistence;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace CaeManager.Infrastructure.AltaAutonoma;

/// <summary>
/// <see cref="IPreparacionContrasenaAlta"/> con los validadores y el hasher de Identity de la aplicación (longitud,
/// mayúscula, minúscula y dígito). La cuenta candidata no existe: ningún validador consulta la base.
/// </summary>
public sealed class PreparacionContrasenaAlta(UserManager<ApplicationUser> usuarios) : IPreparacionContrasenaAlta
{
    public async Task<string?> ValidarYHashearAsync(string correo, string contrasena)
    {
        if (string.IsNullOrEmpty(contrasena))
            return null;

        var candidata = new ApplicationUser { Email = correo, UserName = correo };
        foreach (var validador in usuarios.PasswordValidators)
        {
            if (!(await validador.ValidateAsync(usuarios, candidata, contrasena)).Succeeded)
                return null;
        }

        return usuarios.PasswordHasher.HashPassword(candidata, contrasena);
    }

    /// <summary>
    /// Mismo hasher que creó el hash (PBKDF2 de Identity, comparación en tiempo constante). Un hash de una versión antigua
    /// que pida volver a calcularse también casa: la cuenta aún no existe y el hash se copiará tal cual en el alta. Un hash
    /// que no es Base64 hace lanzar <see cref="FormatException"/> al hasher de Identity: aquí es «no casa», no un error 500.
    /// </summary>
    public bool Coincide(string hashContrasena, string contrasena)
    {
        if (string.IsNullOrEmpty(hashContrasena) || string.IsNullOrEmpty(contrasena))
            return false;

        try
        {
            return usuarios.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), hashContrasena, contrasena)
                != PasswordVerificationResult.Failed;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>
    /// Hash fijo del proceso para <see cref="CompararFicticia"/>: lo genera el mismo hasher, con sus parámetros vigentes
    /// (versión, iteraciones, sal), a partir de un valor aleatorio que nadie conoce. Se calcula una vez por proceso, en el
    /// primer rechazo: solo ese paga un PBKDF2 más.
    /// </summary>
    private static string? _hashFicticio;

    public void CompararFicticia(string? contrasena)
    {
        var hash = Volatile.Read(ref _hashFicticio);
        if (hash is null)
        {
            var nuevo = usuarios.PasswordHasher.HashPassword(new ApplicationUser(), Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
            hash = Interlocked.CompareExchange(ref _hashFicticio, nuevo, null) ?? nuevo;
        }

        // Una contraseña vacía también paga el PBKDF2: el rechazo real por contraseña vacía pasa por aquí.
        _ = usuarios.PasswordHasher.VerifyHashedPassword(new ApplicationUser(), hash, string.IsNullOrEmpty(contrasena) ? "-" : contrasena);
    }
}

/// <summary>
/// <see cref="IAccesoSolicitudesAlta"/> sobre las funciones <c>SECURITY DEFINER</c> de la migración
/// <c>AccesoSolicitudesAltaPorFunciones</c>. No pasa por el <c>DbSet</c>: <c>SolicitudesAlta</c> está cerrada a
/// <c>cae_app_runtime</c> y así sigue.
/// </summary>
public sealed class AccesoSolicitudesAlta(CaeManagerDbContext contexto) : IAccesoSolicitudesAlta
{
    public async Task IniciarAsync(SolicitudAlta solicitud, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(solicitud);
        if (solicitud.Estado != EstadoSolicitudAlta.Iniciada)
            throw new InvalidOperationException("Solo se registra una Solicitud de alta recién iniciada.");

        await contexto.Database.ExecuteSqlAsync($"""
            SELECT public.app_alta_iniciar_solicitud(
                {solicitud.Id}, {solicitud.Correo}, {solicitud.CorreoNormalizado}, {solicitud.HashContrasena},
                {solicitud.HashTokenVerificacion}, {solicitud.TokenVerificacionCaducaEnUtc}, {solicitud.IpOrigen},
                {solicitud.UserAgentOrigen}, {solicitud.CreadaEnUtc}, {solicitud.CaducaEnUtc})
            """, cancellationToken);
    }

    public async Task<EnlaceVerificacionAlta> ConsultarEnlaceAsync(string token, CancellationToken cancellationToken)
    {
        if (HashOrNull(token) is not { } hash)
            return EnlaceVerificacionAlta.NoValido;

        var fila = (await contexto.Database
            .SqlQuery<FilaEnlace>($"""
                SELECT resultado AS "Resultado", hash_contrasena AS "HashContrasena"
                  FROM public.app_alta_consultar_enlace({hash})
                """)
            .ToListAsync(cancellationToken)).Single();

        return fila.Resultado == "Vigente"
            ? new EnlaceVerificacionAlta(null, fila.HashContrasena)
            : new EnlaceVerificacionAlta(ResultadoVerificacion(fila.Resultado), null);
    }

    public async Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(
        string token, string hashContrasenaComprobado, string credencialContinuacion, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(hashContrasenaComprobado);
        if (HashOrNull(token) is not { } hash)
            return ResultadoVerificacionCorreoAlta.NoValido;

        // El credencial lo genera la aplicación (ServicioRegistroAlta.NuevoToken): si fuera corto es un fallo de programa.
        var hashCredencial = SolicitudAlta.CalcularHashToken(credencialContinuacion);

        var resultado = (await contexto.Database
            .SqlQuery<string>($"""
                SELECT public.app_alta_verificar_correo({hash}, {hashContrasenaComprobado}, {hashCredencial}) AS "Value"
                """)
            .ToListAsync(cancellationToken)).Single();

        return ResultadoVerificacion(resultado);
    }

    public async Task<TransicionDatosOrganizacionAlta> CompletarDatosOrganizacionAsync(
        string credencialContinuacion, DatosOrganizacionAlta datos, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(datos);
        if (HashOrNull(credencialContinuacion) is not { } hashCredencial)
            return new TransicionDatosOrganizacionAlta(ResultadoDatosOrganizacionAlta.NoValido, null);

        var tramo = datos.Tramo.ToString();
        var fila = (await contexto.Database
            .SqlQuery<FilaDatosOrganizacion>($"""
                SELECT resultado AS "Resultado", solicitud_alta_id AS "SolicitudAltaId"
                  FROM public.app_alta_completar_datos_organizacion(
                      {hashCredencial}, {datos.RazonSocial}, {datos.CifNif}, {datos.DomicilioFiscal}, {datos.Telefono},
                      {tramo}, {datos.DeclaraRepresentacion}, {datos.DeclaraActividadProfesional})
                """)
            .ToListAsync(cancellationToken)).Single();

        return Enum.TryParse<ResultadoDatosOrganizacionAlta>(fila.Resultado, out var valor) && Enum.IsDefined(valor)
            ? new TransicionDatosOrganizacionAlta(valor, valor == ResultadoDatosOrganizacionAlta.NoValido ? null : fila.SolicitudAltaId)
            : throw new InvalidOperationException($"Resultado de los datos de la organización desconocido: {fila.Resultado}.");
    }

    public async Task<EstadoContinuacionAlta> ConsultarContinuacionAsync(string credencialContinuacion, CancellationToken cancellationToken)
    {
        if (HashOrNull(credencialContinuacion) is not { } hashCredencial)
            return EstadoContinuacionAlta.NoValido;

        var resultado = (await contexto.Database
            .SqlQuery<string>($"""
                SELECT public.app_alta_consultar_continuacion({hashCredencial}) AS "Value"
                """)
            .ToListAsync(cancellationToken)).Single();

        return Enum.TryParse<EstadoContinuacionAlta>(resultado, out var valor) && Enum.IsDefined(valor)
            ? valor
            : throw new InvalidOperationException($"Estado de la continuación del alta desconocido: {resultado}.");
    }

    private static string? HashOrNull(string valor)
    {
        try
        {
            return SolicitudAlta.CalcularHashToken(valor);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static ResultadoVerificacionCorreoAlta ResultadoVerificacion(string resultado) =>
        Enum.TryParse<ResultadoVerificacionCorreoAlta>(resultado, out var valor) && Enum.IsDefined(valor)
            ? valor
            : throw new InvalidOperationException($"Resultado de verificación desconocido: {resultado}.");

    internal sealed class FilaEnlace
    {
        public string Resultado { get; set; } = string.Empty;
        public string? HashContrasena { get; set; }
    }

    internal sealed class FilaDatosOrganizacion
    {
        public string Resultado { get; set; } = string.Empty;
        public Guid? SolicitudAltaId { get; set; }
    }
}

/// <summary>
/// Unicidad global de la cuenta por correo con <c>app_correo_en_uso_por_otra_cuenta</c>, que ya usa
/// <c>AlmacenUsuarios</c>: responde sí o no sin revelar la cuenta ni su Tenant. El Id vacío no es ninguna cuenta.
/// </summary>
public sealed class ExistenciaCuentaPorCorreo(CaeManagerDbContext contexto) : IExistenciaCuentaPorCorreo
{
    public Task<bool> ExisteAsync(string correoNormalizado, CancellationToken cancellationToken) =>
        contexto.Database
            .SqlQuery<bool>($"SELECT app_correo_en_uso_por_otra_cuenta({correoNormalizado}, {Guid.Empty}) AS \"Value\"")
            .SingleAsync(cancellationToken);
}

/// <summary>
/// Remitente de desarrollo del alta autónoma: escribe cada correo como un fichero HTML en un directorio local y nunca
/// usa SMTP. Lo lee el E2E local (A9) para seguir el enlace. Solo se registra en <c>Development</c>.
/// </summary>
public sealed class RemitenteCorreoAltaDesarrollo(
    IOptions<AltaAutonomaOptions> opciones, TimeProvider reloj, ILogger<RemitenteCorreoAltaDesarrollo> logger) : IRemitenteCorreoAlta
{
    public async Task<Result> EnviarAsync(string destinatario, string asunto, string cuerpoHtml, CancellationToken cancellationToken)
    {
        var directorio = DirectorioEfectivo(opciones.Value);
        Directory.CreateDirectory(directorio);
        var ruta = Path.Combine(directorio, $"{reloj.GetUtcNow():yyyyMMddHHmmssfff}-{Guid.NewGuid():N}.html");
        var contenido = $"""
            <!-- Para: {System.Net.WebUtility.HtmlEncode(destinatario)} -->
            <!-- Asunto: {System.Net.WebUtility.HtmlEncode(asunto)} -->
            {cuerpoHtml}
            """;
        await File.WriteAllTextAsync(ruta, contenido, cancellationToken);
        logger.LogInformation("Correo del alta autónoma escrito en {Ruta} (remitente de desarrollo, sin SMTP).", ruta);
        return Result.Exito();
    }

    public static string DirectorioEfectivo(AltaAutonomaOptions opciones) =>
        string.IsNullOrWhiteSpace(opciones.DirectorioCorreoDesarrollo)
            ? Path.Combine(Path.GetTempPath(), "talveg-correo-alta")
            : opciones.DirectorioCorreoDesarrollo;
}

/// <summary>Remitente del alta autónoma fuera de <c>Development</c>: el canal de correo de la aplicación, con su guarda.</summary>
public sealed class RemitenteCorreoAltaPorCanal(IEmailService correo) : IRemitenteCorreoAlta
{
    public Task<Result> EnviarAsync(string destinatario, string asunto, string cuerpoHtml, CancellationToken cancellationToken) =>
        correo.EnviarAsync(destinatario, asunto, cuerpoHtml, TipoAvisoCorreo.Transaccional, cancellationToken: cancellationToken);
}

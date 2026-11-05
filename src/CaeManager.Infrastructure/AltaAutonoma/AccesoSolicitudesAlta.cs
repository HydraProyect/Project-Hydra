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

    public async Task<ResultadoVerificacionCorreoAlta> VerificarCorreoAsync(string token, CancellationToken cancellationToken)
    {
        string hash;
        try
        {
            hash = SolicitudAlta.CalcularHashToken(token);
        }
        catch (ArgumentException)
        {
            return ResultadoVerificacionCorreoAlta.NoValido;
        }

        var resultado = await contexto.Database
            .SqlQuery<string>($"SELECT public.app_alta_verificar_correo({hash}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        return Enum.TryParse<ResultadoVerificacionCorreoAlta>(resultado, out var valor)
            ? valor
            : throw new InvalidOperationException($"Resultado de verificación desconocido: {resultado}.");
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

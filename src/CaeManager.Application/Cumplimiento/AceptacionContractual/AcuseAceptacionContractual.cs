using System.Globalization;
using System.Net;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Usuarios;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento;

namespace CaeManager.Application.Cumplimiento.AceptacionContractual;

/// <summary>
/// Envía por correo a una cuenta del Tenant actual el texto de lo que el Tenant propietario tiene
/// aceptado (#2009): la aceptación más reciente de cada documento, con su versión, su fecha, su hash, el
/// importe de permanencia mostrado y el texto completo de esa versión. Cada envío queda como una
/// <see cref="AcuseAceptacionContractualTenant"/> por aceptación, y solo si el correo salió.
///
/// <para>
/// Lo usan el alta, después de confirmar su transacción, y el reenvío que pide el Administrador del
/// Tenant propietario (<see cref="ReenviarAcuseAceptacionContractualCommand"/>). Quien lo llama decide
/// a qué cuenta; aquí solo se comprueba que la cuenta sea del Tenant actual, porque se envía a su correo.
/// </para>
/// </summary>
public interface IEnvioAcuseAceptacionContractual
{
    Task<Result> EnviarAsync(Guid usuarioDestinatarioId, CancellationToken cancellationToken = default);
}

public sealed class EnvioAcuseAceptacionContractual(
    IAceptacionContractualTenantRepository repositorio,
    ICatalogoDocumentosContractuales catalogo,
    IGestionCuentasUsuario cuentas,
    IEmailService correo,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IEnvioAcuseAceptacionContractual
{
    public const string Asunto = "Tu contrato con TALVEG: confirmación";

    public static readonly Error SinAceptaciones = Error.Crear(
        "AceptacionContractual.Acuse.SinAceptaciones", "Esta organización no tiene ninguna aceptación registrada.");

    public static readonly Error CuentaNoValida = Error.Crear(
        "AceptacionContractual.Acuse.CuentaNoValida", "No podemos enviar el acuse a esta cuenta.");

    public static readonly Error TextoNoDisponible = Error.Crear(
        "AceptacionContractual.Acuse.TextoNoDisponible", "No encontramos el texto de una versión aceptada.");

    public async Task<Result> EnviarAsync(Guid usuarioDestinatarioId, CancellationToken cancellationToken = default)
    {
        var cuenta = await cuentas.ObtenerAsync(usuarioDestinatarioId, cancellationToken);
        if (cuenta is not { EsPropiaDelTenantActual: true, Activa: true } || string.IsNullOrWhiteSpace(cuenta.Email))
            return Result.Fallo(CuentaNoValida);

        // La más reciente de cada documento: es la que obliga hoy. ListarAsync ya viene ordenada.
        var aceptaciones = (await repositorio.ListarAsync(cancellationToken))
            .GroupBy(a => a.Documento)
            .Select(g => g.First())
            .OrderBy(a => a.Documento, StringComparer.Ordinal)
            .ToList();
        if (aceptaciones.Count == 0)
            return Result.Fallo(SinAceptaciones);

        var documentos = new List<(AceptacionContractualTenant Aceptacion, DocumentoContractualPublicado Publicado)>();
        foreach (var aceptacion in aceptaciones)
        {
            // Se envía el texto cuyo hash es el aceptado, no el de la versión de hoy.
            var publicado = catalogo.Publicados.FirstOrDefault(d =>
                d.Documento == aceptacion.Documento && d.Version == aceptacion.Version && d.HashSha256 == aceptacion.HashSha256);
            if (publicado is null)
                return Result.Fallo(TextoNoDisponible);
            documentos.Add((aceptacion, publicado));
        }

        var envio = await correo.EnviarAsync(
            cuenta.Email, Asunto, Cuerpo(documentos), TipoAvisoCorreo.Transaccional, cancellationToken: cancellationToken);
        if (envio.EsFallido)
            return envio;

        var enviadoEnUtc = reloj.GetUtcNow().UtcDateTime;
        foreach (var (aceptacion, _) in documentos)
            repositorio.AgregarAcuse(new AcuseAceptacionContractualTenant(aceptacion.Id, usuarioDestinatarioId, enviadoEnUtc));
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return Result.Exito();
    }

    private static string Cuerpo(IReadOnlyList<(AceptacionContractualTenant Aceptacion, DocumentoContractualPublicado Publicado)> documentos)
    {
        var espanol = CultureInfo.GetCultureInfo("es-ES");
        var html = new StringBuilder();
        html.Append("<p>Estos son los documentos que tu organización tiene aceptados en TALVEG, con el texto exacto de cada versión.</p>");
        foreach (var (aceptacion, publicado) in documentos)
        {
            html.Append("<h2>").Append(Html(publicado.Titulo)).Append("</h2>");
            html.Append("<p>Versión: ").Append(Html(aceptacion.Version))
                .Append("<br>Aceptado el ").Append(Html(aceptacion.AceptadaEnUtc.EnHoraPeninsular().ToString("dd/MM/yyyy HH:mm", espanol))).Append(" (hora peninsular)")
                .Append("<br>Huella SHA-256 del texto: ").Append(Html(aceptacion.HashSha256));
            if (aceptacion.ImportePermanencia is { } importe)
                html.Append("<br>Importe de permanencia mostrado al aceptar: ")
                    .Append(Html(importe.ToString("N2", espanol))).Append(" €, sin impuestos");
            html.Append("</p>");
            html.Append("<pre style=\"white-space:pre-wrap\">").Append(Html(publicado.Texto)).Append("</pre>");
        }

        return html.ToString();
    }

    private static string Html(string texto) => WebUtility.HtmlEncode(texto);
}

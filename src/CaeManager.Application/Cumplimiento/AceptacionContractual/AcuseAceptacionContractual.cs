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
/// importe de permanencia mostrado y el texto completo de esa versión.
///
/// <para>
/// Bandeja de salida de solo inserción (<see cref="AcuseAceptacionContractualTenant"/>): primero se guardan
/// las filas <see cref="FaseAcuseAceptacionContractual.Preparado"/> de este envío, y solo si ese guardado se
/// confirma sale el correo; después se añade el resultado, Enviado o Fallido. Así nunca sale un correo sin
/// que conste, y nunca se afirma un envío que el servicio de correo no aceptó. Si el correo sale y falla el
/// guardado del resultado, el envío queda en Preparado: de resultado incierto, sin afirmar de más.
/// </para>
///
/// <para>
/// Lo usan el alta, después de confirmar su transacción (este servicio guarda por su cuenta, y el guardado
/// previo al correo tiene que ser definitivo, no parte de una transacción que aún se pueda deshacer: con una
/// transacción abierta o cambios pendientes en la unidad de trabajo se niega con <c>EnvioNoAislado</c>), y el
/// reenvío que pide el Administrador del Tenant propietario (<see cref="ReenviarAcuseAceptacionContractualCommand"/>).
/// Quien lo llama decide a qué cuenta; aquí solo se comprueba que la cuenta sea del Tenant actual, porque se
/// envía a su correo.
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

    public static readonly Error EnvioNoAislado = Error.Crear(
        "AceptacionContractual.Acuse.EnvioNoAislado", "No podemos enviar el acuse en este momento.");

    public async Task<Result> EnviarAsync(Guid usuarioDestinatarioId, CancellationToken cancellationToken = default)
    {
        // El guardado previo al correo tiene que ser definitivo y solo de este envío: ni dentro de una
        // transacción que aún se pueda deshacer, ni arrastrando cambios pendientes de quien llama.
        if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes)
            return Result.Fallo(EnvioNoAislado);

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

        var cuerpo = Cuerpo(documentos);
        var envioId = IdentificadorEntidad.Nuevo();

        // 1. Lo que se va a enviar queda guardado antes de enviarlo. Si este guardado falla, la excepción
        //    sube y el correo no sale.
        AgregarFase(documentos, usuarioDestinatarioId, envioId, FaseAcuseAceptacionContractual.Preparado);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // 2. El envío, sin cancelación: con la preparación ya confirmada, cortarlo a medias solo dejaría más
        //    envíos de resultado incierto.
        var envio = await correo.EnviarAsync(cuenta.Email, Asunto, cuerpo, TipoAvisoCorreo.Transaccional, cancellationToken: CancellationToken.None);

        // 3. El resultado. Si este guardado falla, el envío se queda en Preparado: incierto, no afirmado.
        AgregarFase(documentos, usuarioDestinatarioId, envioId,
            envio.EsFallido ? FaseAcuseAceptacionContractual.Fallido : FaseAcuseAceptacionContractual.Enviado);
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        return envio.EsFallido ? envio : Result.Exito();
    }

    private void AgregarFase(
        IReadOnlyList<(AceptacionContractualTenant Aceptacion, DocumentoContractualPublicado Publicado)> documentos,
        Guid usuarioDestinatarioId, Guid envioId, FaseAcuseAceptacionContractual fase)
    {
        var ahoraUtc = reloj.GetUtcNow().UtcDateTime;
        foreach (var (aceptacion, _) in documentos)
            repositorio.AgregarAcuse(new AcuseAceptacionContractualTenant(aceptacion.Id, usuarioDestinatarioId, envioId, fase, ahoraUtc));
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

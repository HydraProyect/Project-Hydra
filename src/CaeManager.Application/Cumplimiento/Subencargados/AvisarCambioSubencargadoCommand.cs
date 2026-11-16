using System.Globalization;
using System.Net;
using System.Text;
using CaeManager.Application.Common;
using CaeManager.Application.Plataforma;
using CaeManager.Application.Tenants.Encargo;
using CaeManager.Domain.Common;
using CaeManager.Domain.Cumplimiento.Subencargados;
using FluentValidation;
using MediatR;

namespace CaeManager.Application.Cumplimiento.Subencargados;

/// <summary>
/// Avisa de un subencargado propuesto a los Administradores de todos los Tenants propietarios activos (#2010, D-7).
///
/// <para>
/// La primera vez pasa el subencargado a Avisado, lo que fija la fecha mínima de activación (aviso +
/// <see cref="Subencargado.PlazoMinimoAviso"/>). Se puede repetir: un Tenant que ya tiene un aviso
/// <see cref="FaseAvisoCambioSubencargado.Enviado"/> no se vuelve a avisar, y uno sin él (un envío que no se
/// confirmó, o un Tenant dado de alta o reactivado después) recibe el suyo, cuyo plazo cuenta desde ese envío.
/// </para>
///
/// <para>
/// Bandeja de salida de solo inserción, como el acuse de la aceptación contractual: por cada Administrador se guarda
/// primero la fila Preparado, en el ámbito de su Tenant propietario; solo si ese guardado se confirma sale el correo,
/// fuera de toda transacción; después se guarda Enviado o NoConfirmado. Con una transacción abierta o cambios
/// pendientes en la unidad de trabajo se niega con <see cref="ErroresSubencargado.EnvioNoAislado"/>.
/// </para>
/// </summary>
[AreaDelEncargo("Cumplimiento")]
public record AvisarCambioSubencargadoCommand(Guid SubencargadoId) : ICommand<ResultadoAvisoSubencargado>;

/// <summary>
/// Recuento por Tenant propietario activo: avisado ahora (al menos un Administrador con envío confirmado), ya avisado
/// antes, sin confirmar (ningún envío confirmado) y sin destinatario (ningún Administrador con correo).
/// </summary>
public sealed record ResultadoAvisoSubencargado(
    int TenantsAvisados, int TenantsYaAvisados, int TenantsSinConfirmar, int TenantsSinDestinatario);

public class AvisarCambioSubencargadoCommandValidator : AbstractValidator<AvisarCambioSubencargadoCommand>
{
    public AvisarCambioSubencargadoCommandValidator() => RuleFor(c => c.SubencargadoId).NotEmpty();
}

public class AvisarCambioSubencargadoCommandHandler(
    ICurrentUserService usuarioActual,
    ISesionPrivilegiadaActual sesionPrivilegiada,
    IAutorizacionAdminPlataforma autorizacion,
    ISubencargadoRepository repositorio,
    IEmailService correo,
    IUnitOfWork unitOfWork,
    TimeProvider reloj)
    : IRequestHandler<AvisarCambioSubencargadoCommand, Result<ResultadoAvisoSubencargado>>
{
    public const string Asunto = "TALVEG: aviso de un subencargado nuevo";

    public async Task<Result<ResultadoAvisoSubencargado>> Handle(AvisarCambioSubencargadoCommand request, CancellationToken cancellationToken)
    {
        var actor = await AutorizacionSubencargados.ActorDePlataformaAsync(usuarioActual, sesionPrivilegiada, autorizacion, cancellationToken);
        if (actor.EsFallido)
            return Result.Fallo<ResultadoAvisoSubencargado>(actor.Error);

        // Cada guardado previo a un correo tiene que ser definitivo y solo de este envío.
        if (repositorio.HayTransaccionAbierta || repositorio.HayCambiosPendientes)
            return Result.Fallo<ResultadoAvisoSubencargado>(ErroresSubencargado.EnvioNoAislado);

        var subencargado = await repositorio.ObtenerAsync(request.SubencargadoId, cancellationToken);
        if (subencargado is null)
            return Result.Fallo<ResultadoAvisoSubencargado>(ErroresSubencargado.NoEncontrado);
        if (subencargado.Estado is not (EstadoSubencargado.Propuesto or EstadoSubencargado.Avisado))
            return Result.Fallo<ResultadoAvisoSubencargado>(ErroresSubencargado.NoSeAvisa);

        if (subencargado.Estado == EstadoSubencargado.Propuesto)
        {
            subencargado.MarcarAvisado(reloj.GetUtcNow().UtcDateTime);
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        int avisados = 0, yaAvisados = 0, sinConfirmar = 0, sinDestinatario = 0;
        foreach (var tenantId in await repositorio.TenantsPropietariosActivosAsync(cancellationToken))
        {
            // Las filas del aviso son del Tenant propietario avisado: se leen y se sellan en su ámbito.
            using var _ = AmbitoTenantExplicito.Establecer(tenantId);

            if (await repositorio.TieneAvisoProbadoAsync(subencargado.Id, null, cancellationToken))
            {
                yaAvisados++;
                continue;
            }

            var administradores = await repositorio.AdministradoresDelTenantActualAsync(tenantId, cancellationToken);
            if (administradores.Count == 0)
            {
                sinDestinatario++;
                continue;
            }

            // La fecha que se promete a este Tenant es la suya: la activación exige un aviso Enviado suyo de hace al
            // menos el plazo, y ese aviso se registra después de este instante, así que no puede activarse antes.
            var cuerpo = Cuerpo(subencargado, FechaMinimaParaEsteTenant(subencargado, Ahora()));
            var algunoEnviado = false;
            foreach (var administrador in administradores)
                algunoEnviado |= await EnviarAsync(subencargado.Id, administrador, cuerpo, actor.Valor, cancellationToken);

            if (algunoEnviado)
                avisados++;
            else
                sinConfirmar++;
        }

        return Result.Exito(new ResultadoAvisoSubencargado(avisados, yaAvisados, sinConfirmar, sinDestinatario));
    }

    private async Task<bool> EnviarAsync(
        Guid subencargadoId, DestinatarioAvisoSubencargado destinatario, string cuerpo, Guid actorId, CancellationToken cancellationToken)
    {
        var envioId = IdentificadorEntidad.Nuevo();

        // 1. Lo que se va a enviar queda guardado antes de enviarlo. Si este guardado falla, la excepción sube y el
        //    correo no sale.
        repositorio.AgregarAviso(new AvisoCambioSubencargado(
            subencargadoId, envioId, destinatario.UsuarioId, FaseAvisoCambioSubencargado.Preparado, Ahora(), actorId));
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // 2. El envío, sin cancelación: con la preparación ya confirmada, cortarlo a medias solo dejaría más envíos de
        //    resultado incierto. Pie Transaccional, como el acuse de la aceptación contractual: no hay un
        //    TipoAvisoCorreo para avisos contractuales o de servicio, y los otros pies dicen algo falso aquí
        //    (Seguridad: «si no has sido tú»; Informativo: «por tu responsabilidad de coordinación»).
        var envio = await correo.EnviarAsync(destinatario.Email, Asunto, cuerpo, TipoAvisoCorreo.Transaccional, cancellationToken: CancellationToken.None);

        // 3. El resultado. Si este guardado falla, el envío se queda en Preparado: incierto, no afirmado, y no cuenta
        //    como aviso probado.
        var fase = envio.EsFallido ? FaseAvisoCambioSubencargado.NoConfirmado : FaseAvisoCambioSubencargado.Enviado;
        repositorio.AgregarAviso(new AvisoCambioSubencargado(subencargadoId, envioId, destinatario.UsuarioId, fase, Ahora(), actorId));
        await unitOfWork.SaveChangesAsync(CancellationToken.None);
        return fase == FaseAvisoCambioSubencargado.Enviado;
    }

    private DateTime Ahora() => reloj.GetUtcNow().UtcDateTime;

    /// <summary>
    /// La fecha antes de la cual no se activa el subencargado, para un Tenant al que se avisa en
    /// <paramref name="avisoUtc"/>: su aviso más el plazo, y nunca antes de la fecha mínima del subencargado.
    /// </summary>
    internal static DateTime FechaMinimaParaEsteTenant(Subencargado subencargado, DateTime avisoUtc)
    {
        var propia = avisoUtc + Subencargado.PlazoMinimoAviso;
        return subencargado.FechaMinimaActivacionUtc is { } global && global > propia ? global : propia;
    }

    /// <summary>
    /// Texto neutro y factual, sin contenido contractual: nombre, finalidad, ubicación, la fecha mínima de activación
    /// para ese Tenant y cómo objetar. Hoy no hay pantalla ni endpoint para objetar, así que el canal que se ofrece es
    /// la respuesta a este correo.
    /// </summary>
    internal static string Cuerpo(Subencargado subencargado, DateTime fechaMinimaUtc)
    {
        var espanol = CultureInfo.GetCultureInfo("es-ES");
        var fechaMinima = fechaMinimaUtc.EnHoraPeninsular().ToString("dd/MM/yyyy", espanol);
        var html = new StringBuilder();
        html.Append("<p>TALVEG va a incorporar un subencargado nuevo del tratamiento.</p>");
        html.Append("<p>Nombre: ").Append(Html(subencargado.Nombre))
            .Append("<br>Finalidad: ").Append(Html(subencargado.Finalidad))
            .Append("<br>Ubicación y transferencia: ").Append(Html(subencargado.UbicacionYTransferencia))
            .Append("<br>No se activará antes del ").Append(Html(fechaMinima)).Append(".</p>");
        html.Append("<p>El Administrador de tu organización puede objetar respondiendo a este correo, antes de esa fecha o después. ");
        html.Append(subencargado.AdmiteExclusionPorTenant
            ? "Si objetas, tu organización dejará de usar este subencargado; las demás seguirán usándolo.</p>"
            : "Este subencargado no se puede excluir para una sola organización: si objetas, quedará constancia de tu objeción.</p>");
        return html.ToString();
    }

    private static string Html(string texto) => WebUtility.HtmlEncode(texto);
}

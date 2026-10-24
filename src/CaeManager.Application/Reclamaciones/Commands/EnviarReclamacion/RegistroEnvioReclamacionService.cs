using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Commands.EnviarMensajeNuevo;
using CaeManager.Application.Integraciones;
using CaeManager.Application.Reclamaciones.Eventos;
using CaeManager.Domain.Common;
using CaeManager.Domain.Documentos;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Reclamaciones;
using MediatR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;

/// <summary>
/// A quién se le reclama: una Empresa contraparte, más la posición que ocupa
/// (ADR-011 — "Cliente" y titular de documentación de empresa son posiciones,
/// no tipos de entidad distintos). <see cref="Ambito"/> decide qué ancla de
/// <see cref="ReclamacionDocumental"/> se rellena.
/// </summary>
/// <param name="Ambito">Solo <c>Cliente</c> o <c>Empresa</c>: son los dos únicos titulares que el modelo admite hoy.</param>
/// <param name="DocumentosDeTrabajadores">
/// Solo con <c>Ambito = Empresa</c>: la Empresa es la que emplea a los Trabajadores cuyos documentos se reclaman
/// (<see cref="ReclamacionDocumental.ParaEmpresaEmpleadora"/>), no la propietaria de documentos de empresa.
/// </param>
public sealed record TitularReclamacion(Guid Id, string RazonSocial, AmbitoAplicacion Ambito, bool DocumentosDeTrabajadores = false);

/// <summary>
/// Cola común de todo envío de reclamación, sea cual sea el ámbito del
/// titular: elegir buzón, mandar el correo (por buzón conectado o por
/// <see cref="IEmailService"/>), registrar la <see cref="ReclamacionDocumental"/>
/// y publicar el evento.
///
/// Existe como servicio, y no duplicada en cada comando, porque es la parte
/// que NO puede divergir entre ámbitos: el registro append-only del historial,
/// el anclaje de la conversación y el evento del timeline tienen que
/// significar lo mismo se reclame a un Cliente o a una Empresa. Lo que sí
/// difiere de verdad —qué documentos son reclamables y cómo se resuelven sus
/// destinatarios— se queda en cada comando, porque el join es genuinamente
/// distinto (Trabajador→Asignación→Centro frente a Documento.EmpresaId
/// directo).
///
/// <para>
/// El envío es idempotente (<see cref="EnvioReclamacion"/>): antes del primer correo queda guardado un registro previo,
/// cada entrega se guarda en cuanto sale y la reclamación se registra al final. Si falla un guardado posterior a un
/// correo, el resultado es <see cref="RegistroEnvioReclamacionService.CodigoRegistroFallido"/> y reenviar la misma
/// reclamación no vuelve a mandarla a quien ya consta que la recibió. Un fallo del correo nunca la registra.
/// </para>
/// </summary>
public interface IRegistroEnvioReclamacionService
{
    /// <param name="documentosQueFaltan">
    /// Documentos que nunca se subieron y se piden en el mismo correo: no hay Documento al que apuntar, así que el registro
    /// guarda su Tipo y a quién le faltan. Cuentan exactamente igual que los demás: misma reclamación, misma «última».
    /// </param>
    /// <param name="destinatarios">A quién se pide: los contactos de la agenda y los que el gestor añadió a mano (el servicio no distingue).</param>
    /// <param name="enCopia">
    /// Direcciones en copia (CC). Por buzón conectado van en el campo CC del mismo mensaje; por SMTP, donde cada destinatario recibe su
    /// propio correo, cada dirección en copia recibe una copia suelta DESPUÉS de los destinatarios, y solo si a alguno de ellos le llegó.
    /// La copia suelta lleva arriba una nota que dice que es una copia y a quién se envió (<see cref="CuerpoDeCopia"/>), porque por SMTP
    /// no existe la cabecera CC compartida. El registro las guarda marcadas «(CC)» (<see cref="DestinatariosExtraDeReclamacion.FormatoDeRegistro"/>).
    /// </param>
    Task<Result> EnviarYRegistrarAsync(
        TitularReclamacion titular,
        IReadOnlyList<Guid> documentoIds,
        IReadOnlyList<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        CancellationToken cancellationToken,
        IReadOnlyList<DocumentoQueFaltaPedido>? documentosQueFaltan = null,
        IReadOnlyList<string>? enCopia = null);
}

public class RegistroEnvioReclamacionService(
    IIntegracionesQueryContext integracionesContext,
    IEmailService emailService,
    IReclamacionDocumentalRepository repositorio,
    ICurrentUserService currentUserService,
    ICorreoDelActorReal correoDelActorReal,
    IMediator mediator,
    ILogger<RegistroEnvioReclamacionService> logger,
    IUnitOfWork unitOfWork,
    IEnvioReclamacionRepository envios,
    IDescarteCambiosPendientes descarteCambios) : IRegistroEnvioReclamacionService
{
    /// <summary>
    /// Fallo con efecto: la reclamación llegó a una parte de los
    /// destinatarios y esa parte SÍ quedó registrada. Público para que la UI
    /// sepa que, aun siendo un fallo, el historial cambió y hay que recargarlo.
    /// </summary>
    public const string CodigoEnvioParcial = "Reclamacion.EnvioParcial";

    /// <summary>
    /// Fallo con efecto: el correo salió (a todos o a parte) pero no pudimos guardar que salió. El reintento de la misma
    /// reclamación no lo vuelve a mandar a quien ya lo recibió (<see cref="EnvioReclamacion"/>).
    /// </summary>
    public const string CodigoRegistroFallido = "Reclamacion.RegistroFallido";

    /// <summary>
    /// Cuánto tiempo un envío pendiente se considera reintentable. Pasado este plazo, enviar la misma reclamación es
    /// una reclamación nueva y el registro antiguo queda como rastro de lo que salió sin registrarse.
    /// </summary>
    public static readonly TimeSpan VentanaDeReintento = TimeSpan.FromHours(24);

    public async Task<Result> EnviarYRegistrarAsync(
        TitularReclamacion titular,
        IReadOnlyList<Guid> documentoIds,
        IReadOnlyList<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        CancellationToken cancellationToken,
        IReadOnlyList<DocumentoQueFaltaPedido>? documentosQueFaltan = null,
        IReadOnlyList<string>? enCopia = null)
    {
        enCopia ??= [];
        documentosQueFaltan ??= [];
        var destinatarioUnico = DestinatariosExtraDeReclamacion.FormatoDeRegistro(destinatarios, enCopia);
        IReadOnlyList<string> destinatariosFallidos = [];

        // Antes de que salga nada: sin usuario no hay a quién atribuir la reclamación, y comprobarlo después del
        // correo dejaba el correo salido y sin registrar.
        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Reclamacion.SinUsuario", "No pudimos identificar tu usuario."));

        // Registro previo del envío (Z5-4, revisión de seguridad 2026-10-23). El correo a un tercero no se deshace,
        // así que el orden es: registro «en envío» guardado → correo → entrega guardada → reclamación registrada.
        // Si lo que falla es un guardado posterior al correo, el reintento de la misma reclamación (misma huella)
        // reanuda este registro y no vuelve a mandar el correo a quien ya lo recibió.
        var huella = EnvioReclamacion.CalcularHuella(
            titular.Id, titular.Ambito, titular.DocumentosDeTrabajadores, documentoIds, documentosQueFaltan, destinatarios, enCopia);
        var envioPrevio = await envios.ObtenerPendienteAsync(
            usuarioId.Value, huella, DateTime.UtcNow - VentanaDeReintento, cancellationToken);

        if (envioPrevio is null)
        {
            envioPrevio = EnvioReclamacion.Iniciar(usuarioId.Value, huella, DateTime.UtcNow);
            envios.Agregar(envioPrevio);
            try
            {
                // Con el token de la pantalla: todavía no ha salido nada, cancelar aquí no deja nada a medias.
                await unitOfWork.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // Circuito de Blazor Server: el registro añadido no puede quedarse para el siguiente comando.
                descarteCambios.DescartarCambiosPendientes();
                throw;
            }
        }

        var envio = envioPrevio;

        // Desde aquí puede salir un correo. Ni el envío ni su registro se cortan porque la pantalla se retire: un
        // correo entregado y sin registrar es justo el hueco que esto cierra, y el reintento no puede depender de
        // que el Gestor CAE siga en la página.
        var sinCorte = CancellationToken.None;
        cancellationToken.ThrowIfCancellationRequested();

        // GestorPropietarioId != null excluido a propósito: un buzón personal
        // de un gestor (ConexionIntegracion.GestorPropietarioId) también tiene
        // ClienteId null, igual que el buzón genérico del tenant — sin este
        // filtro, una reclamación de negocio podía salir desde el buzón
        // personal de un gestor cualquiera, sin que él lo supiera ni lo
        // consintiera. Mismo hueco en PedirPrioridadValidacionCommand,
        // ObtenerBorradorPedirPrioridadQuery y MigrarConversacionACorreoCommand
        // (corregido en el mismo cambio).
        //
        // Con titular Empresa solo entra el buzón genérico del tenant: un buzón
        // dedicado lo está a un Cliente Delegante (ConexionIntegracion.ClienteId),
        // y no existe hoy la noción de buzón dedicado a una Empresa contraparte.
        var clienteIdDelBuzon = titular.Ambito == AmbitoAplicacion.Cliente ? (Guid?)titular.Id : null;

        // Un reintento sigue por el carril del intento anterior: si el buzón ya mandó el correo (hay hilo) no se
        // vuelve a mandar, y si el SMTP ya entregó a alguien no se cambia al buzón, que lo mandaría a todos otra vez.
        Guid? conversacionId = envio.ConversacionId;
        var reanudaSmtp = envio.Entregados.Count > 0 || envio.EntregadosEnCopia.Count > 0;

        var conexionId = conversacionId is not null || reanudaSmtp
            ? null
            : await integracionesContext.ConexionesIntegracion
                .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado == EstadoConexionIntegracion.Habilitada)
                .Where(c => c.GestorPropietarioId == null)
                .Where(c => c.ClienteId == null || c.ClienteId == clienteIdDelBuzon)
                .OrderByDescending(c => c.ClienteId != null)
                .Select(c => (Guid?)c.Id)
                .FirstOrDefaultAsync(sinCorte);

        // Con hilo de un intento anterior, el buzón conectado ya lo mandó: no entra en ninguna rama, solo falta registrarlo.
        if (conversacionId is null && conexionId is not null)
        {
            // El hilo se ancla al titular real. Para una Empresa contraparte va
            // por EmpresaId y no por ClienteId: son planos distintos, y dejarlo
            // sin ancla lo mandaría a la cola de triage, que es compartida por
            // toda la gestión CAE (§ 12.4) — el alcance de cartera de la
            // reclamación se perdería en cuanto alguien abriera Comunicaciones.
            var mensaje = titular.Ambito == AmbitoAplicacion.Cliente
                ? new EnviarMensajeNuevoCommand(conexionId.Value, destinatarios, asunto, cuerpoHtml, ClienteId: titular.Id, Copia: enCopia)
                : new EnviarMensajeNuevoCommand(conexionId.Value, destinatarios, asunto, cuerpoHtml, EmpresaId: titular.Id, Copia: enCopia);

            // Si el comando del buzón falla después de que Graph aceptara el correo (hilo de otro titular, o su propio
            // guardado), deja el hilo rastreado sin guardar: se descarta para que no viaje en el guardado de otra
            // operación del mismo circuito. Ese caso residual sí reenvía en el reintento, porque no hay hilo apuntado.
            Result<Guid> envioResultado;
            try
            {
                envioResultado = await mediator.Send(mensaje, sinCorte);
            }
            catch
            {
                descarteCambios.DescartarCambiosPendientes();
                throw;
            }

            if (envioResultado.EsFallido)
            {
                descarteCambios.DescartarCambiosPendientes();
                return Result.Fallo(envioResultado.Error);
            }

            conversacionId = envioResultado.Valor;

            // El hilo se apunta en el registro previo antes de registrar la reclamación: si ese segundo guardado
            // falla, el reintento ve que el buzón ya lo mandó y no lo vuelve a mandar.
            envio.RegistrarConversacion(envioResultado.Valor);
            var hiloGuardado = await GuardarTrasEnviarAsync();
            if (hiloGuardado.EsFallido)
                return hiloGuardado;
        }
        else if (conversacionId is null)
        {
            // Decisión D3 (2026-09-19): la respuesta la recibe el Gestor CAE
            // que emite la reclamación. Por aquí el correo sale del buzón de
            // TALVEG —el From no se toca, ver IEmailService—, así que sin
            // Reply-To la respuesta de la Empresa contraparte moría en ese
            // buzón; era el hueco que dejó #698 al quitar del pie la
            // invitación a responder.
            //
            // El correo sale del actor REAL, nunca de un usuario simulado: es
            // lo que garantiza ICorreoDelActorReal por el carril de auditoría.
            // Hoy, además, ninguna Sesión Privilegiada de soporte llega hasta
            // aquí — AutorizacionEscrituraBehavior deniega toda escritura que
            // no sea de aprovisionamiento antes del handler.
            var responderA = await correoDelActorReal.ObtenerAsync(sinCorte);

            if (responderA is null)
            {
                // Se envía igual: la reclamación es la acción de negocio y no
                // puede depender de que la cuenta tenga correo. Pero queda
                // escrito, porque el destinatario recibirá un correo al que no
                // puede responder — y el pie, sin Reply-To, deja de invitarle
                // a hacerlo.
                logger.LogWarning(
                    "Reclamación enviada por SMTP sin Reply-To: no pudimos resolver el correo de quien la emite. " +
                    "La respuesta del destinatario no llegará a nadie.");
            }

            // Aquí el correo NO es un aviso "best effort" (ver IEmailService):
            // es la acción de negocio misma. Un fallo de SMTP no puede acabar
            // registrado ni anunciado como reclamación enviada — mismo
            // contrato que la rama de buzón conectado, que devuelve el fallo
            // de EnviarMensajeNuevoCommand sin registrar nada.
            //
            // Con varios destinatarios, un correo ya entregado no se puede
            // deshacer: el historial registra solo a quienes sí lo recibieron
            // (es append-only y cuenta lo que pasó), y el resultado sigue
            // siendo un fallo que nombra a quienes no, para que la UI nunca
            // lo pinte como enviado del todo.
            //
            // Un reintento parte de lo que el registro previo ya apunta como entregado: a esos no se les vuelve a mandar.
            var enviados = new List<string>(envio.Entregados);
            var enviadosEnCopia = new List<string>(envio.EntregadosEnCopia);
            var fallidos = new List<string>();

            // Los de copia van después y solo si a algún destinatario le llegó: una copia de un correo que no salió a nadie no informa de nada.
            foreach (var (destinatario, esCopia) in destinatarios.Select(d => (d, false))
                         .Concat(enCopia.Select(c => (c, true))))
            {
                if (esCopia && enviados.Count == 0)
                    break;

                if (envio.YaEntregadoA(destinatario, esCopia))
                    continue;

                // Una excepción del transporte (una dirección mal formada en la agenda, un servicio SMTP caído de un modo que
                // el adaptador no contempla) es un destinatario fallido, no un fallo de todo el envío: dejarla subir tras haber
                // entregado ya a un destinatario anterior dejaba ese correo salido y sin registrar. La cancelación sí sube.
                Result resultadoCorreo;
                try
                {
                    resultadoCorreo = await emailService.EnviarAsync(
                        destinatario, asunto, esCopia ? CuerpoDeCopia(cuerpoHtml, enviados) : cuerpoHtml,
                        TipoAvisoCorreo.Requerimiento, responderA, sinCorte);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    resultadoCorreo = Result.Fallo(Error.Crear("Email.Excepcion", "No pudimos enviar el correo."));
                    logger.LogError(ex, "Reclamación por SMTP: excepción no contemplada al enviar a un destinatario.");
                }

                if (resultadoCorreo.EsExitoso)
                {
                    (esCopia ? enviadosEnCopia : enviados).Add(destinatario);

                    // Cada entrega se guarda en el acto, antes del siguiente correo: si algo corta el envío a medias,
                    // el reintento sabe exactamente a quién ya le llegó.
                    envio.RegistrarEntrega(destinatario, esCopia);
                    var entregaGuardada = await GuardarTrasEnviarAsync();
                    if (entregaGuardada.EsFallido)
                        return entregaGuardada;
                }
                else
                {
                    fallidos.Add(destinatario);
                    logger.LogWarning(
                        "Reclamación por SMTP: el envío a un destinatario falló ({Codigo}: {Mensaje}).",
                        resultadoCorreo.Error.Codigo, resultadoCorreo.Error.Mensaje);
                }
            }

            if (enviados.Count == 0)
            {
                return Result.Fallo(Error.Crear(
                    "Reclamacion.EnvioFallido",
                    "No pudimos enviar el correo de la reclamación, así que no queda registrada. Inténtalo de nuevo en unos minutos."));
            }

            destinatarioUnico = DestinatariosExtraDeReclamacion.FormatoDeRegistro(enviados, enviadosEnCopia);
            destinatariosFallidos = fallidos;
        }

        var reclamacion = titular.Ambito == AmbitoAplicacion.Cliente
            ? ReclamacionDocumental.ParaCliente(titular.Id, usuarioId.Value, destinatarioUnico, DateTime.UtcNow, documentoIds, conversacionId, documentosQueFaltan)
            : titular.DocumentosDeTrabajadores
                ? ReclamacionDocumental.ParaEmpresaEmpleadora(titular.Id, usuarioId.Value, destinatarioUnico, DateTime.UtcNow, documentoIds, conversacionId, documentosQueFaltan)
                : ReclamacionDocumental.ParaEmpresa(titular.Id, usuarioId.Value, destinatarioUnico, DateTime.UtcNow, documentoIds, conversacionId, documentosQueFaltan);

        repositorio.Agregar(reclamacion);

        // En el mismo guardado que la reclamación: o quedan las dos cosas (reclamación y envío completo) o ninguna, y
        // en ese caso el reintento la registra sin volver a mandar el correo.
        envio.MarcarRegistrado(reclamacion.Id);
        var reclamacionGuardada = await GuardarTrasEnviarAsync();
        if (reclamacionGuardada.EsFallido)
            return reclamacionGuardada;

        // Después del commit, no antes: el timeline es un reflejo del hecho, no
        // parte de él. Sin conversación (rama sin buzón conectado) no hay hilo
        // al que avisar.
        if (conversacionId is not null)
            await mediator.Publish(new ReclamacionEnviadaEvent(conversacionId.Value, reclamacion.Id), sinCorte);

        if (destinatariosFallidos.Count > 0)
        {
            return Result.Fallo(Error.Crear(
                CodigoEnvioParcial,
                $"La reclamación solo llegó a {destinatarioUnico}, y así queda registrada. " +
                $"No pudimos enviarla a {string.Join(", ", destinatariosFallidos)}: " +
                "revisa esas direcciones y vuelve a enviarla solo a ellas."));
        }

        return Result.Exito();
    }

    /// <summary>
    /// Todo guardado posterior al primer correo: sin el token de la pantalla, y un fallo no sube como excepción sino
    /// como un resultado que dice que el correo salió, porque la UI tiene que poder decirlo y el Gestor CAE reintentar.
    /// Lo que quedó rastreado se descarta (en Blazor Server el contexto vive lo que el circuito): el reintento relee el
    /// registro previo tal como quedó guardado.
    /// </summary>
    private async Task<Result> GuardarTrasEnviarAsync()
    {
        try
        {
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            return Result.Exito();
        }
        catch (Exception ex)
        {
            descarteCambios.DescartarCambiosPendientes();
            logger.LogError(ex, "Reclamación: el correo salió pero no pudimos guardar su registro.");
            return Result.Fallo(Error.Crear(
                CodigoRegistroFallido,
                "El correo de la reclamación ya ha salido, pero no pudimos guardarla. " +
                "Vuelve a enviarla en unos minutos para que quede registrada."));
        }
    }

    /// <summary>
    /// El cuerpo que recibe una dirección en copia por SMTP: el mismo correo, con una nota arriba que dice que es una copia y a quién se
    /// envió. Sin ella, quien está en copia leería una petición de documentos dirigida a otra persona sin saber por qué le llega.
    /// </summary>
    internal static string CuerpoDeCopia(string cuerpoHtml, IEnumerable<string> destinatarios) =>
        "<p><em>Copia informativa de una reclamación enviada a " +
        System.Net.WebUtility.HtmlEncode(string.Join(", ", destinatarios)) + ".</em></p>" + cuerpoHtml;
}

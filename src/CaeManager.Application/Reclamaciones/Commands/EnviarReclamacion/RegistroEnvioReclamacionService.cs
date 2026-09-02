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

namespace CaeManager.Application.Reclamaciones.Commands.EnviarReclamacion;

/// <summary>
/// A quién se le reclama: una Empresa contraparte, más la posición que ocupa
/// (ADR-011 — "Cliente" y titular de documentación de empresa son posiciones,
/// no tipos de entidad distintos). <see cref="Ambito"/> decide qué ancla de
/// <see cref="ReclamacionDocumental"/> se rellena.
/// </summary>
/// <param name="Ambito">Solo <c>Cliente</c> o <c>Empresa</c>: son los dos únicos titulares que el modelo admite hoy.</param>
public sealed record TitularReclamacion(Guid Id, string RazonSocial, AmbitoAplicacion Ambito);

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
/// </summary>
public interface IRegistroEnvioReclamacionService
{
    Task<Result> EnviarYRegistrarAsync(
        TitularReclamacion titular,
        IReadOnlyList<Guid> documentoIds,
        IReadOnlyList<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        CancellationToken cancellationToken);
}

public class RegistroEnvioReclamacionService(
    IIntegracionesQueryContext integracionesContext,
    IEmailService emailService,
    IReclamacionDocumentalRepository repositorio,
    ICurrentUserService currentUserService,
    IMediator mediator,
    IUnitOfWork unitOfWork) : IRegistroEnvioReclamacionService
{
    public async Task<Result> EnviarYRegistrarAsync(
        TitularReclamacion titular,
        IReadOnlyList<Guid> documentoIds,
        IReadOnlyList<string> destinatarios,
        string asunto,
        string cuerpoHtml,
        CancellationToken cancellationToken)
    {
        var destinatarioUnico = string.Join("; ", destinatarios);

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

        var conexionId = await integracionesContext.ConexionesIntegracion
            .Where(c => c.Proveedor == ProveedorIntegracion.Microsoft365 && c.Estado == EstadoConexionIntegracion.Habilitada)
            .Where(c => c.GestorPropietarioId == null)
            .Where(c => c.ClienteId == null || c.ClienteId == clienteIdDelBuzon)
            .OrderByDescending(c => c.ClienteId != null)
            .Select(c => (Guid?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        Guid? conversacionId = null;
        if (conexionId is not null)
        {
            // El hilo se ancla al titular real. Para una Empresa contraparte va
            // por EmpresaId y no por ClienteId: son planos distintos, y dejarlo
            // sin ancla lo mandaría a la cola de triage, que es compartida por
            // toda la gestión CAE (§ 12.4) — el alcance de cartera de la
            // reclamación se perdería en cuanto alguien abriera Comunicaciones.
            var envio = titular.Ambito == AmbitoAplicacion.Cliente
                ? new EnviarMensajeNuevoCommand(conexionId.Value, destinatarios, asunto, cuerpoHtml, ClienteId: titular.Id)
                : new EnviarMensajeNuevoCommand(conexionId.Value, destinatarios, asunto, cuerpoHtml, EmpresaId: titular.Id);

            var envioResultado = await mediator.Send(envio, cancellationToken);
            if (envioResultado.EsFallido)
                return Result.Fallo(envioResultado.Error);

            conversacionId = envioResultado.Valor;
        }
        else
        {
            foreach (var destinatario in destinatarios)
                await emailService.EnviarAsync(destinatario, asunto, cuerpoHtml, cancellationToken);
        }

        var usuarioId = await currentUserService.ObtenerUsuarioActualIdAsync();
        if (usuarioId is null)
            return Result.Fallo(Error.Crear("Reclamacion.SinUsuario", "No pudimos identificar tu usuario."));

        var reclamacion = titular.Ambito == AmbitoAplicacion.Cliente
            ? ReclamacionDocumental.ParaCliente(titular.Id, usuarioId.Value, destinatarioUnico, DateTime.UtcNow, documentoIds, conversacionId)
            : ReclamacionDocumental.ParaEmpresa(titular.Id, usuarioId.Value, destinatarioUnico, DateTime.UtcNow, documentoIds, conversacionId);

        repositorio.Agregar(reclamacion);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Después del commit, no antes: el timeline es un reflejo del hecho, no
        // parte de él. Sin conversación (rama sin buzón conectado) no hay hilo
        // al que avisar.
        if (conversacionId is not null)
            await mediator.Publish(new ReclamacionEnviadaEvent(conversacionId.Value, reclamacion.Id), cancellationToken);

        return Result.Exito();
    }
}

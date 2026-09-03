using CaeManager.Application.Common;
using System.Text.Json;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Subcontratas;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Infrastructure.Auditing;

/// <summary>
/// Registra en RegistroAuditoria cada alta/modificación/baja de una entidad
/// de dominio (ver ARCHITECTURE.md, "Auditoría y soft delete"). Enmascara
/// antes de serializar las propiedades de PropiedadesSensiblesPorTipo, que
/// hoy cubre dos familias distintas:
///
/// 1. Secretos cifrados por ValueConverter en CaeManagerDbContext
///    (CanalGestionDocumental, CredencialAccesoEmpresa,
///    CredencialAccesoSubcontrata, CredencialIntegracion, SuscripcionWebhook,
///    LineaWhatsApp): nunca deben quedar en texto plano en el historial (ver
///    DATABASE.md). Esta lista debe crecer junto con cualquier propiedad
///    nueva que se cifre en reposo — el cifrado en la BD no protege el
///    historial de auditoría, que lee el valor plano directamente del
///    ChangeTracker antes de que el ValueConverter lo cifre.
/// 2. Contenido de Comunicaciones que la auditoría no necesita copiar
///    (Mensaje, AdjuntoMensaje) — ver el comentario de esas dos entradas.
/// 3. Datos personales de entidades hermanas del mensaje y contenido
///    derivado por IA a partir de él (ParticipanteConversacion,
///    ContactoWhatsApp, ClasificacionRelevanciaCae, SugerenciaGestionCorreo,
///    SugerenciaVisitaCorreo) — DEC-37/38 extienden el criterio del punto 2:
///    ni el remitente en una fila distinta ni un resumen generado a partir
///    del correo quedan exentos por ello. Ver el comentario de esas entradas.
/// </summary>
public class AuditoriaInterceptor(IActorAuditoria actorAuditoria) : SaveChangesInterceptor
{
    private static readonly Dictionary<Type, HashSet<string>> PropiedadesSensiblesPorTipo = new()
    {
        [typeof(CanalGestionDocumental)] = [nameof(CanalGestionDocumental.Usuario), nameof(CanalGestionDocumental.Contrasena)],
        [typeof(CredencialAccesoEmpresa)] = [nameof(CredencialAccesoEmpresa.Usuario), nameof(CredencialAccesoEmpresa.Contrasena)],
        [typeof(CredencialAccesoSubcontrata)] = [nameof(CredencialAccesoSubcontrata.Usuario), nameof(CredencialAccesoSubcontrata.Contrasena)],
        [typeof(CredencialIntegracion)] = [nameof(CredencialIntegracion.RefreshToken), nameof(CredencialIntegracion.AccessToken)],
        [typeof(SuscripcionWebhook)] = [nameof(SuscripcionWebhook.ClientState)],
        [typeof(LineaWhatsApp)] = [nameof(LineaWhatsApp.TokenAcceso)],

        // DEC-9 (propietario, 2026-09-02): a diferencia de las entradas de
        // arriba, estas tres propiedades no son secretos cifrados en reposo —
        // son CONTENIDO. El rastro de auditoría existe para decir quién cambió
        // qué y cuándo, no para copiar el correo: cuerpo, remitente y nombre
        // del adjunto siguen viviendo en las filas Mensajes/AdjuntosMensaje,
        // así que enmascararlos aquí no quita ninguna capacidad forense. Lo
        // que sí quita es una segunda vía de lectura del buzón sin las puertas
        // de Comunicaciones: /auditoria la lee el rol Administrador, que no es
        // el rol con acceso a la bandeja, y CuerpoHtml puede llegar a 1 MiB
        // (Mensaje.LongitudMaximaCuerpoHtml) en cada alta de mensaje.
        [typeof(Mensaje)] = [nameof(Mensaje.CuerpoHtml), nameof(Mensaje.Remitente)],
        [typeof(AdjuntoMensaje)] = [nameof(AdjuntoMensaje.NombreArchivo)],

        // DEC-37/DEC-38 (propietario, 2026-09-02): extienden DEC-9 a las
        // entidades hermanas del mensaje y a su contenido derivado por IA.
        // DEC-37 — "No crear una excepción simplemente porque el PII esté en
        // una entidad hermana del mensaje": el email de un participante y el
        // teléfono/nombre de un contacto de WhatsApp identifican a una
        // persona igual que Mensaje.Remitente, aunque vivan en su propia
        // fila. DEC-38 — un resumen generado por IA a partir del correo
        // tampoco es "menos sensible que el contenido fuente" por el mero
        // hecho de ser derivado: sería una vía indirecta para reconstruirlo.
        // La auditoría conserva que cambió, quién y cuándo — nunca el texto
        // del resumen ni el valor de estos campos.
        [typeof(ParticipanteConversacion)] = [nameof(ParticipanteConversacion.Email)],
        [typeof(ContactoWhatsApp)] = [nameof(ContactoWhatsApp.Telefono), nameof(ContactoWhatsApp.Nombre)],
        [typeof(ClasificacionRelevanciaCae)] = [nameof(ClasificacionRelevanciaCae.Resumen)],
        [typeof(SugerenciaGestionCorreo)] = [nameof(SugerenciaGestionCorreo.Resumen)],
        [typeof(SugerenciaVisitaCorreo)] = [nameof(SugerenciaVisitaCorreo.Resumen)]
    };

    public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is DbContext context)
        {
            var actor = await actorAuditoria.ObtenerAsync();
            var registros = ConstruirRegistros(context, actor);

            if (registros.Count > 0)
                context.Set<RegistroAuditoria>().AddRange(registros);
        }

        return await base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    /// <summary>
    /// La vía síncrona también audita — este interceptor era el único de los
    /// tres sin este override, exactamente la clase de agujero por omisión
    /// que el hallazgo N-15 (INFORME-AUDITORIA-2.md) cerró en los otros dos:
    /// un SaveChanges() corriente guardaba sin dejar rastro de auditoría.
    /// Diferencia con TenantSelladoInterceptor: aquí el trabajo compartido
    /// necesita el usuario actual, cuyo contrato es asíncrono. Bloquear con
    /// GetResult() sobre un Task pendiente arriesga deadlock en el circuito
    /// Blazor, así que solo se aprovecha si ya está resuelto (el caso normal:
    /// los claims están cacheados) y si no, se audita sin autoría — un
    /// registro sin usuario es mejor que ningún registro, y es lo mismo que
    /// ya ocurre con los jobs de fondo.
    ///
    /// Lo que sí cambió: ese registro sin autoría se marca ahora con vía
    /// <c>Desconocida</c> en vez de pasar por un acceso normal anónimo. La
    /// diferencia importa cuando exista la impersonación — ADR-011 § 8.5
    /// exige que una sesión privilegiada no pueda auditarse sin actor, y para
    /// prohibirlo primero hay que poder distinguir "no lo sé" de "fue normal".
    /// </summary>
    public override InterceptionResult<int> SavingChanges(
        DbContextEventData eventData,
        InterceptionResult<int> result)
    {
        if (eventData.Context is DbContext context)
        {
            var actor = actorAuditoria.ObtenerSiYaEstaResuelto() ?? ActorAuditoria.SinResolver;
            var registros = ConstruirRegistros(context, actor);

            if (registros.Count > 0)
                context.Set<RegistroAuditoria>().AddRange(registros);
        }

        return base.SavingChanges(eventData, result);
    }

    private static List<RegistroAuditoria> ConstruirRegistros(DbContext context, ActorAuditoria actor)
    {
        var registros = new List<RegistroAuditoria>();
        var via = (TipoViaAccesoAuditoria)actor.Via;

        foreach (var entrada in context.ChangeTracker.Entries())
        {
            if (entrada.Entity is RegistroAuditoria) continue;
            if (entrada.Entity.GetType().Namespace?.StartsWith("CaeManager.Domain", StringComparison.Ordinal) != true) continue;
            if (entrada.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var accion = entrada.State switch
            {
                EntityState.Added => "Creado",
                EntityState.Modified => "Modificado",
                EntityState.Deleted => "Eliminado",
                _ => "Desconocido"
            };

            var entidadId = entrada.Property("Id").CurrentValue as Guid? ?? Guid.Empty;
            var sensibles = PropiedadesSensiblesPorTipo.GetValueOrDefault(entrada.Entity.GetType());

            // En Added/Deleted no hay "propiedad modificada" que distinguir —
            // se registra la fila entera. En Modified sí: filtrar a
            // p.IsModified evita duplicar en Antes/Después el resto de la
            // entidad (comentarios, PII, payloads) cuando solo cambió un
            // campo, y reduce a la mitad el tamaño de cada fila de auditoría
            // en el caso común de una edición puntual.
            var propiedades = entrada.State == EntityState.Modified
                ? entrada.Properties.Where(p => p.IsModified)
                : entrada.Properties;

            string? datosAntes = entrada.State != EntityState.Added
                ? SerializarValores(propiedades, sensibles, usarValorOriginal: true)
                : null;
            string? datosDespues = entrada.State != EntityState.Deleted
                ? SerializarValores(propiedades, sensibles, usarValorOriginal: false)
                : null;

            registros.Add(new RegistroAuditoria(
                entrada.Entity.GetType().Name, entidadId, accion, datosAntes, datosDespues,
                actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
                actor.ActorRealUsuarioId, via, actor.ViaAccesoId));
        }

        return registros;
    }

    // DEUDA CONOCIDA, no olvido: los actos sobre los catálogos globales de
    // asignación operativa (Domain.Operaciones) se auditan aquí contra el
    // tenant que ITenantActual tenga resuelto, que en cuatro de los cinco
    // caminos NO es el del propietario de los datos — crear o revocar una
    // delegación se hace desde el tenant de la consultora o el de plataforma.
    // El resultado es que el cliente cuyo reparto se está tocando no ve el
    // acto en su propia auditoría, y ADR-011 § 5 dice que debería.
    //
    // No se arregla excluyéndolos de aquí: eso solo quita la fila del sitio
    // equivocado y deja el hueco igual. Hace falta escribirla contra el tenant
    // propietario con su propio ámbito explícito, y eso es un SaveChanges
    // aparte — el patrón de RegistroActividadSoporte. Va en su propio cambio,
    // con la pérdida de transaccionalidad que implica decidida a la vista.

    private static string SerializarValores(
        IEnumerable<PropertyEntry> propiedades, HashSet<string>? propiedadesSensibles, bool usarValorOriginal)
    {
        var valores = propiedades.ToDictionary(
            p => p.Metadata.Name,
            object? (p) => propiedadesSensibles?.Contains(p.Metadata.Name) == true
                ? "***"
                : usarValorOriginal ? p.OriginalValue : p.CurrentValue);

        return JsonSerializer.Serialize(valores);
    }
}

using CaeManager.Application.Common;
using System.Text.Json;
using CaeManager.Domain.Auditoria;
using CaeManager.Domain.Centros;
using CaeManager.Domain.Comunicaciones;
using CaeManager.Domain.Empresas;
using CaeManager.Domain.Integraciones;
using CaeManager.Domain.Subcontratas;
using CaeManager.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CaeManager.Infrastructure.Auditing;

/// <summary>
/// Registra en RegistroAuditoria cada alta/modificación/baja de una entidad
/// de dominio (ver ARCHITECTURE.md, "Auditoría y soft delete"), y también de
/// las cuatro entidades de Identity que dejan rastro de quién gestiona a
/// quién y de cómo se entra a una cuenta: <see cref="ApplicationUser"/> (alta,
/// edición, baja, activación/desactivación de una cuenta),
/// <see cref="IdentityUserRole{TKey}"/> (concesión y revocación de un rol — el
/// caso más grave, "quién hizo Administrador a quién", ver
/// CIERRE-TURNO-NOCTURNO-2026-09-18.md § 12), <see cref="IdentityUserLogin{TKey}"/>
/// (vinculación de un login externo/SSO) e <see cref="IdentityUserToken{TKey}"/>
/// (el secreto del segundo factor). Identity vive fuera del namespace
/// CaeManager.Domain (Infrastructure.Identity), así que estas cuatro entran
/// por una excepción explícita en vez de por el filtro de namespace — ver
/// <see cref="TiposDeIdentidadAuditados"/> y ConstruirRegistros. Enmascara antes de serializar las
/// propiedades de PropiedadesSensiblesPorTipo, que hoy cubre estas familias:
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
/// 4. Secretos de autenticación de <see cref="ApplicationUser"/>
///    (PasswordHash, SecurityStamp) y de <see cref="IdentityUserToken{TKey}"/>
///    (Value — el secreto TOTP del segundo factor, y las claves de
///    recuperación): Identity los guarda en la misma fila que los datos que
///    sí interesa auditar (rol, activación, nombre, qué proveedor y qué
///    token), así que no se puede excluir la entidad entera — solo estos
///    campos. Un registro de auditoría que copiara el secreto TOTP sería una
///    segunda vía de suplantar el segundo factor, legible por el rol
///    Administrador desde /auditoria.
/// </summary>
public class AuditoriaInterceptor(IActorAuditoria actorAuditoria) : SaveChangesInterceptor
{
    /// <summary>
    /// Las entidades de Identity que se auditan pese a vivir fuera del
    /// namespace de dominio, y el <c>EntidadTipo</c> en castellano con el que
    /// se archiva cada una. Es la ÚNICA fuente de ese conjunto: la usan
    /// <see cref="ConstruirRegistros"/> para decidir si una entrada entra,
    /// <see cref="ResolverTipoEId"/> para nombrarla, y el trinquete
    /// <c>EscritorasDeIdentitySinAuditarTests</c> para congelarla. Los
    /// nombres viven en <see cref="EntidadTipoAuditoria"/> (Domain) porque
    /// también los consumen <c>TenantSelladoInterceptor</c> y la pantalla
    /// /auditoria — ver el comentario de esa clase.
    ///
    /// <para>
    /// <b>Hueco declarado, no olvido: las bajas en cascada</b> (hallazgo de la
    /// revisión de Codex, misión N6/V4). Este interceptor solo ve lo que está
    /// en el <c>ChangeTracker</c>, y las tres tablas de relación cuelgan de
    /// <c>AspNetUsers</c> con <c>ON DELETE CASCADE</c> (migración
    /// <c>LineaBase</c>). Cuando algo borra una cuenta sin cargar antes sus
    /// roles, sus logins y sus tokens —hoy, <c>RetiradaTenantDemoService</c>—,
    /// PostgreSQL borra esas filas sin que EF llegue a verlas: queda auditada
    /// la baja de la cuenta ("Usuario / Eliminado") pero no la de cada fila
    /// dependiente. No lo abre este cambio: es el mismo hueco que tiene
    /// "RolDeUsuario" desde #704, y se acepta con el mismo criterio —
    /// desaparecen porque desapareció la cuenta, y esa sí deja rastro. Cerrarlo
    /// exigiría que quien borra cargue las dependientes, y eso es un cambio de
    /// ese servicio, no de este interceptor.
    /// </para>
    /// </summary>
    public static readonly IReadOnlyDictionary<Type, string> TiposDeIdentidadAuditados =
        new Dictionary<Type, string>
        {
            [typeof(ApplicationUser)] = EntidadTipoAuditoria.Usuario,
            [typeof(IdentityUserRole<Guid>)] = EntidadTipoAuditoria.RolDeUsuario,
            [typeof(IdentityUserLogin<Guid>)] = EntidadTipoAuditoria.LoginExterno,
            [typeof(IdentityUserToken<Guid>)] = EntidadTipoAuditoria.TokenDeUsuario,
        };

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
        [typeof(SugerenciaVisitaCorreo)] = [nameof(SugerenciaVisitaCorreo.Resumen)],

        // Punto 4 del comentario de clase: secretos de autenticación de
        // Identity. PasswordHash y SecurityStamp cambian en el mismo
        // SaveChanges que un alta de cuenta o un restablecimiento de
        // contraseña — el resto de la fila (rol, activación, nombre) sí
        // interesa auditar, así que se enmascaran los dos campos, no se
        // excluye la entidad.
        [typeof(ApplicationUser)] = [nameof(ApplicationUser.PasswordHash), nameof(ApplicationUser.SecurityStamp)],

        // Mismo punto 4: el VALOR del token es el secreto. En
        // AspNetUserTokens ese valor es, para el proveedor
        // "[AspNetUserStore]" / nombre "AuthenticatorKey", la clave TOTP en
        // claro (Identity no la cifra en reposo) — copiarla al historial la
        // pondría al alcance de /auditoria, que lee el rol Administrador. Lo
        // que SÍ queda es qué proveedor y qué token cambiaron, y en qué
        // cuenta, que es lo que el registro existe para responder. Las claves
        // de recuperación de dos factores comparten tabla y quedan
        // enmascaradas por el mismo camino.
        [typeof(IdentityUserToken<Guid>)] = [nameof(IdentityUserToken<Guid>.Value)]
    };

    /// <summary>
    /// Propiedades cuyo cambio, si es el ÚNICO en un <c>Modified</c>, no
    /// genera fila de auditoría — a diferencia de PropiedadesSensiblesPorTipo,
    /// que enmascara el VALOR pero deja la fila. Sin esta lista,
    /// <c>ActividadUsuarioService</c> escribiría un "Modificado" de
    /// <see cref="ApplicationUser"/> por cada usuario activo cada minuto
    /// (throttle de <c>ActividadUsuarioService.ThrottleEscritura</c>) — ruido
    /// que ahogaría el propio caso que esta auditoría existe para responder
    /// ("quién hizo Administrador a quién"). Si el campo silenciado cambia
    /// JUNTO con otro que no lo está, la fila se genera igual (con el
    /// silenciado incluido) — la exclusión es del disparo, no del campo.
    /// </summary>
    private static readonly Dictionary<Type, HashSet<string>> PropiedadesSilenciosasPorTipo = new()
    {
        [typeof(ApplicationUser)] = [nameof(ApplicationUser.UltimaActividadUtc), nameof(ApplicationUser.ConcurrencyStamp)]
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

        // Se resuelve UNA vez por SaveChanges, fuera del bucle: el tipo de actor
        // es del acto, no de cada entidad que el acto toque, y
        // ResolverTipoActor() lee un AsyncLocal. El cast entre los dos enums es
        // el mismo patrón que la vía de arriba (Domain no referencia
        // Application), y por eso los números de los dos espejos coinciden.
        var tipoActor = (TipoActorAuditoria)actor.ResolverTipoActor();

        foreach (var entrada in context.ChangeTracker.Entries())
        {
            if (entrada.Entity is RegistroAuditoria) continue;

            // DEC-36 (REC-099): sin esta exclusión, insertar un
            // RegistroAccesoDocumentoSensible generaba TAMBIÉN una fila en
            // RegistroAuditoria ("Creado", con el mismo UsuarioId) — visible
            // en /auditoria por cualquier Administrador, sin pasar por
            // Policies.ConsultarAccesoDocumentosSensibles. No revela
            // DocumentoId/Sensibilidad (DatosDespues no se muestra en el
            // listado general), pero sí quién accedió a un documento
            // sensible y cuándo — justo lo que el permiso específico existe
            // para acotar. Codex lo detectó antes de abrir la PR.
            if (entrada.Entity is RegistroAccesoDocumentoSensible) continue;

            var esDominio = entrada.Entity.GetType().Namespace?.StartsWith("CaeManager.Domain", StringComparison.Ordinal) == true;
            var esIdentidadAuditada = TiposDeIdentidadAuditados.ContainsKey(entrada.Entity.GetType());
            if (!esDominio && !esIdentidadAuditada) continue;
            if (entrada.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;

            var accion = entrada.State switch
            {
                EntityState.Added => "Creado",
                EntityState.Modified => "Modificado",
                EntityState.Deleted => "Eliminado",
                _ => "Desconocido"
            };

            var (entidadTipo, entidadId) = ResolverTipoEId(entrada);
            var sensibles = PropiedadesSensiblesPorTipo.GetValueOrDefault(entrada.Entity.GetType());
            var silenciosas = PropiedadesSilenciosasPorTipo.GetValueOrDefault(entrada.Entity.GetType());

            // En Added/Deleted no hay "propiedad modificada" que distinguir —
            // se registra la fila entera. En Modified sí: filtrar a
            // p.IsModified evita duplicar en Antes/Después el resto de la
            // entidad (comentarios, PII, payloads) cuando solo cambió un
            // campo, y reduce a la mitad el tamaño de cada fila de auditoría
            // en el caso común de una edición puntual.
            var propiedades = entrada.State == EntityState.Modified
                ? entrada.Properties.Where(p => p.IsModified).ToList()
                : entrada.Properties.ToList();

            // Ver PropiedadesSilenciosasPorTipo: un Modified cuyo ÚNICO
            // cambio sea un campo silenciado no genera fila. Solo aplica a
            // Modified — un alta o una baja siempre se registra entera,
            // aunque el campo silenciado esté entre sus propiedades.
            if (entrada.State == EntityState.Modified && silenciosas is not null
                && propiedades.All(p => silenciosas.Contains(p.Metadata.Name)))
                continue;

            string? datosAntes = entrada.State != EntityState.Added
                ? SerializarValores(propiedades, sensibles, usarValorOriginal: true)
                : null;
            string? datosDespues = entrada.State != EntityState.Deleted
                ? SerializarValores(propiedades, sensibles, usarValorOriginal: false)
                : null;

            registros.Add(new RegistroAuditoria(
                entidadTipo, entidadId, accion, datosAntes, datosDespues,
                usuarioId: actor.UsuarioSimuladoId ?? actor.ActorRealUsuarioId,
                tipoActor: tipoActor,
                actorRealUsuarioId: actor.ActorRealUsuarioId,
                viaAcceso: via,
                viaAccesoId: actor.ViaAccesoId));
        }

        return registros;
    }

    /// <summary>
    /// El nombre de tipo y el Id bajo los que se archiva la fila. Las tres
    /// tablas de relación de Identity no tienen propiedad "Id" (su clave es
    /// compuesta: UserId+RoleId, LoginProvider+ProviderKey,
    /// UserId+LoginProvider+Name), así que el camino genérico
    /// (<c>entrada.Property("Id")</c>) lanzaría; en las tres se usa UserId
    /// como EntidadId — es "a quién le pasó esto", que es exactamente lo que
    /// estos registros existen para responder, y lo que la pantalla
    /// /auditoria resuelve a un nombre de cuenta. El nombre de tipo de las
    /// cuatro entidades de Identity se sustituye por uno en castellano (ver
    /// <see cref="TiposDeIdentidadAuditados"/>) en vez del nombre de clase de
    /// Identity — mismo criterio que el resto de EntidadTipo, que ya son
    /// nombres de dominio en castellano (Cliente, Empresa, Trabajador...).
    /// </summary>
    private static (string Tipo, Guid Id) ResolverTipoEId(EntityEntry entrada) => entrada.Entity switch
    {
        ApplicationUser usuario => (EntidadTipoAuditoria.Usuario, usuario.Id),
        IdentityUserRole<Guid> rol => (EntidadTipoAuditoria.RolDeUsuario, rol.UserId),
        IdentityUserLogin<Guid> login => (EntidadTipoAuditoria.LoginExterno, login.UserId),
        IdentityUserToken<Guid> token => (EntidadTipoAuditoria.TokenDeUsuario, token.UserId),
        _ => (entrada.Entity.GetType().Name, entrada.Property("Id").CurrentValue as Guid? ?? Guid.Empty)
    };

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

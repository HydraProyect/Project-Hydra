using Microsoft.AspNetCore.Identity;

namespace CaeManager.Infrastructure.Identity;

/// <summary>Usuario interno de CAE Manager. Extiende Identity solo con lo que ya se necesita en Fase 0.</summary>
public class ApplicationUser : IdentityUser<Guid>
{
    public string NombreCompleto { get; set; } = string.Empty;
    public TemaPreferido Tema { get; set; } = TemaPreferido.Sistema;

    /// <summary>
    /// Idioma de la interfaz de esta cuenta: la fuente canónica de la
    /// preferencia. La cookie de cultura (<c>.AspNetCore.Culture</c>) es solo
    /// su proyección en un navegador concreto — se reescribe desde aquí en
    /// cada inicio de sesión (local, 2FA y Microsoft) y al cambiarla desde el
    /// selector, así que borrar cookies o entrar desde otro dispositivo no
    /// pierde la preferencia. Cambiar de Tenant no la cambia.
    /// </summary>
    public IdiomaPreferido Idioma { get; set; } = IdiomaPreferido.Espanol;

    /// <summary>
    /// Solo relevante para el rol GestorCae — el CoordinadorCae al que
    /// reporta (ver Roles.cs y IAlcanceDatosService). Un GestorCae sin
    /// coordinador asignado solo es visible para Administrador/DireccionCae
    /// hasta que se le asigne uno. Guid suelto (sin navegación EF) — mismo
    /// patrón que EntidadBase.EliminadoPorUsuarioId, porque Identity no
    /// tiene FK real hacia sí mismo por conveniencia de migraciones.
    /// </summary>
    public Guid? CoordinadorUsuarioId { get; set; }

    /// <summary>
    /// Solo relevante para el rol Cliente — la Empresa contraparte
    /// (CaeManager.Domain.Empresas.Empresa) que este usuario representa.
    /// Se vincula por CIF al crear el usuario (ver Usuarios.razor,
    /// BuscarEmpresaPorCifQuery). Nombre físico "ClienteId" conservado por
    /// ahora (F4.2a cambió la semántica, no la columna); un rename físico se
    /// evalúa aparte si hace falta. Antes de F4.2a
    /// apuntaba a la tabla legacy <c>Clientes</c> — ya retirada de este
    /// flujo, ver f4-diseno-fisico-relacionempresarial-2026-08-26.md. Un
    /// usuario Cliente sin ClienteId no ve ningún dato (alcance vacío, no
    /// alcance total) — nunca se interpreta null aquí como "sin
    /// restricción", a diferencia del resto de roles.
    /// </summary>
    public Guid? ClienteId { get; set; }

    /// <summary>
    /// True cuando un Administrador acaba de crear este usuario con una
    /// contraseña temporal (ver Usuarios.razor.cs) y todavía no la ha
    /// cambiado por una propia. Se comprueba tras cada inicio de sesión
    /// (ver Login.razor) y se hace cumplir en cada navegación desde
    /// MainLayout — nunca se marca en true para el Administrador inicial
    /// (IdentitySeeder) ni para los usuarios de DatosPruebaSeeder, porque
    /// ambos usan una contraseña conocida y elegida deliberadamente, no una
    /// temporal de un tercero.
    /// </summary>
    public bool DebeCambiarContrasena { get; set; }

    /// <summary>
    /// Cuándo se creó la cuenta — Identity no trae esto de serie. Usado para
    /// ordenar la lista de "Pendientes de asignar" (ver Roles.razor): un
    /// usuario sin ningún rol asignado (típicamente auto-provisionado por
    /// login de Microsoft, ver IdentityEndpointsExtensions) queda ahí hasta
    /// que un Administrador le asigna uno.
    /// </summary>
    public DateTime FechaCreacion { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// Tenant al que pertenece este usuario (ver docs/MULTITENANCY.md § 8,
    /// Tenant Resolution Strategy — se estampa como claim de sesión al
    /// autenticar, ver TenantClaimsPrincipalFactory). Todo usuario nuevo
    /// debe crearse con un TenantId explícito (Etapa 3 de
    /// PLAN-MIGRACION-MULTITENANT.md — cierre). Sin filtro global aplicado
    /// a esta tabla (a diferencia del resto del dominio): el login necesita
    /// poder resolver el usuario, y por tanto su tenant, antes de conocerlo.
    /// <c>NormalizedUserName</c>/<c>NormalizedEmail</c> se mantienen únicos
    /// globalmente en v1 (no por tenant) — limitación aceptada mientras la
    /// resolución de tenant sea por claim y no por subdominio.
    /// </summary>
    public Guid TenantId { get; set; }

    /// <summary>
    /// Última interacción autenticada con la plataforma, en cualquier
    /// pantalla — no solo el Home (docs/blueprints/OPERATIONAL-HOME.md § 6,
    /// DDL-068). La escribe <c>ActividadUsuarioService</c> desde MainLayout
    /// en cada navegación, con un throttle de un minuto para no escribir en
    /// cada clic. Alimenta el resumen de ausencia: null significa "nunca
    /// tuvo actividad registrada todavía" (usuario recién creado).
    /// </summary>
    public DateTime? UltimaActividadUtc { get; set; }

    /// <summary>
    /// DEC-36 (REC-099): «Administrador del Tenant propietario, mediante
    /// permiso específico; no los gestores ordinarios por defecto». El rol
    /// Administrador es necesario pero no suficiente para consultar el
    /// rastro de acceso a documentos sensibles
    /// (<c>Domain.Auditoria.RegistroAccesoDocumentoSensible</c>) — hace
    /// falta además este permiso, concedido explícitamente por otro
    /// Administrador (ver <c>Usuarios.razor.cs</c>). Empieza en <c>false</c>
    /// para todo usuario nuevo, incluido un Administrador recién creado:
    /// nadie hereda esta consulta solo por el rol. Viaja como claim de
    /// sesión (ver <c>TenantClaimsPrincipalFactory</c>), mismo criterio que
    /// <see cref="DebeCambiarContrasena"/>, para que la política de
    /// autorización no dependa de una consulta a base en cada petición.
    /// </summary>
    public bool PermisoConsultarAccesoDocumentosSensibles { get; set; }

    /// <summary>
    /// «Cuenta desactivada» tal como la deja <c>Desactivar</c> en Usuarios:
    /// <c>LockoutEnd</c> indefinido (<see cref="DateTimeOffset.MaxValue"/>). Se
    /// decide por «más de un año en el futuro» y no por <c>IsLockedOut</c>
    /// a secas, a propósito: el bloqueo temporal por intentos fallidos
    /// (15 minutos, ver IdentityOptions.Lockout) también es un
    /// <c>LockoutEnd</c> vigente, y si tumbara sesiones ya abiertas cualquiera
    /// que conociera un correo podría expulsar a esa persona con cinco
    /// contraseñas erróneas. Solo la desactivación deliberada corta sesiones.
    /// </summary>
    public bool EstaDesactivada(DateTimeOffset ahora) =>
        LockoutEnd is { } fin && fin > ahora.Add(UmbralDeCuentaDesactivada);

    /// <summary>
    /// Lo que <see cref="Desactivar"/> escribe en <c>LockoutEnd</c>. Con
    /// <see cref="EstaDesactivada"/> forma un par: cambiar uno sin el otro
    /// dejaría cuentas desactivadas que ya no lo parecen.
    /// </summary>
    public static readonly DateTimeOffset FinDeBloqueoDeCuentaDesactivada = DateTimeOffset.MaxValue;

    /// <summary>
    /// Desactiva la cuenta: bloqueo indefinido <b>y</b> security stamp nuevo, en
    /// la misma escritura que persista quien llame (un solo <c>UpdateAsync</c>).
    /// El bloqueo impide entrar y, con <see cref="EstaDesactivada"/>, corta las
    /// sesiones abiertas; el stamp es lo que impide que <see cref="Reactivar"/>
    /// devuelva la vida a una cookie o a un token de extensión emitidos antes
    /// (con solo el bloqueo, quitarlo resucitaba lo anterior). Juntos en un
    /// método para que ninguna pantalla pueda hacer una mitad sin la otra.
    /// </summary>
    public void Desactivar()
    {
        LockoutEnabled = true;
        LockoutEnd = FinDeBloqueoDeCuentaDesactivada;
        SecurityStamp = Guid.NewGuid().ToString();
    }

    /// <summary>
    /// Quita el bloqueo. No toca el security stamp: el que rotó en
    /// <see cref="Desactivar"/> sigue manteniendo muertas las sesiones anteriores.
    /// </summary>
    public void Reactivar()
    {
        LockoutEnabled = true;
        LockoutEnd = null;
    }

    /// <summary>Un bloqueo más largo que esto no es un castigo temporal: es una desactivación.</summary>
    public static readonly TimeSpan UmbralDeCuentaDesactivada = TimeSpan.FromDays(365);
}

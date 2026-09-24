namespace CaeManager.Application.Common;

/// <summary>
/// Marcador: este <see cref="ICommand"/> escribe exclusivamente datos del propio
/// usuario que lo ejecuta —su aceptación de términos, sus filtros guardados, sus
/// preferencias, sus notificaciones— y por eso <c>AutorizacionEscrituraBehavior</c>
/// lo deja pasar a cualquier rol reconocido, incluidos los de solo lectura
/// (Consulta y Cliente). Solo lectura significa no tocar los datos del Tenant, no
/// quedarse sin poder cerrar el modal de términos o marcar una notificación como leída.
///
/// Lo que el comando tiene que cumplir para llevar el marcador, las tres cosas a la vez:
/// <list type="number">
/// <item>El usuario sobre el que escribe sale de <see cref="ICurrentUserService"/>,
/// nunca de un parámetro del request.</item>
/// <item>Lo que escribe es del usuario (lleva su <c>UsuarioId</c>) y nadie más lo ve
/// ni lo usa: ni configuración del Tenant, ni datos de negocio CAE, ni métricas que
/// lean otros (por eso <c>RegistrarTramoGestionCommand</c> y
/// <c>RegistrarHistorialInformeCommand</c> se quedan fuera). La traza que
/// <c>AuditoriaInterceptor</c> deja de cualquier escritura no cuenta: registra quién
/// hizo qué y la ve el Administrador del Tenant, pero nadie opera con ella, y en la
/// aceptación de términos es precisamente la prueba que interesa conservar.</item>
/// <item>Si el handler busca o modifica algo por un Id del request, comprueba que es
/// del usuario actual y responde igual cuando no existe y cuando es de otro: un
/// mensaje distinto revelaría que el Id existe. Un Id que solo se guarda como
/// etiqueta en la fila propia, sin desreferenciarlo (el <c>EntidadId</c> y la
/// <c>UrlDestino</c> de <c>RegistrarUsoRecienteCommand</c>), no abre nada: esa fila
/// solo la lee su dueño.</item>
/// </list>
///
/// No abre nada a las sesiones privilegiadas de plataforma: el behavior las decide
/// antes que el rol, y la inspección de soporte sigue siendo de solo lectura sin
/// excepción. Tampoco a un usuario sin rol reconocible: la lista blanca de roles
/// sigue mandando, solo deja de exigir uno de escritura.
///
/// Sin miembros a propósito, igual que <see cref="IComandoDeAprovisionamiento"/>: la
/// lista es explícita y la congela <c>ComandosDeAutoservicioInventariadosTests</c>
/// (Architecture.Tests), para que entrar aquí sea una decisión visible en revisión.
/// </summary>
public interface IComandoDeAutoservicio;

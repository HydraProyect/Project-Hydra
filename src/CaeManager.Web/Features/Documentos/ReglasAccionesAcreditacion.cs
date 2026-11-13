using CaeManager.Domain.Documentos;
using CaeManager.Infrastructure.Identity;
using CaeManager.Web.Features.Centros.Components;

namespace CaeManager.Web.Features.Documentos;

/// <summary>Lo que el Gestor CAE elige en el menú «Acreditación ▾» de una fila (el portal es un enlace y no pasa por aquí).</summary>
public enum AccionAcreditacion
{
    SubirVersionCorregida,
    MarcarSubido,
    MarcarValidado,
    MarcarRechazado,
    AnotarVigencia,
    Gestionar
}

/// <summary>
/// Qué acciones de acreditación ofrece una fila según su estado (la regla que tenía la pestaña Plataformas CAE, PlataformaTab, y que
/// ahora comparten esa pestaña y el menú «Acreditación ▾» del listado agrupado por Plataforma CAE, D-18). Solo decide qué se
/// muestra: cada Command comprueba su autorización y su alcance de datos, y el rol de quien mira lo decide la pantalla (D-3).
/// <para>
/// Deuda declarada: PlataformaTab usa tres de estos predicados (subir la corregida, marcar subido y abrir portal); sus «Marcar
/// validado», «Marcar rechazado…» y «Anotar vigencia» siguen con su condición en línea, y en una acreditación «No exigida» ofrece
/// las dos primeras mientras el menú no. Unificarlo cambiaría el comportamiento de la pestaña, que sale con el rediseño sin pestañas.
/// </para>
/// </summary>
public static class ReglasAccionesAcreditacion
{
    /// <summary>
    /// Quien puede recibir «Abrir portal» y ver el motivo literal de rechazo: todos los roles del Tenant salvo Cliente, el mismo
    /// corte que ObtenerCentroIdsParaGestionAsync. El rol Cliente sí ve el estado en la plataforma (opción B, 2026-11-12).
    /// </summary>
    public const string RolesQueAbrenPortal = $"{Roles.ConEscrituraCsv},{Roles.Consulta}";

    /// <summary>
    /// «Subir versión corregida»: solo una Rechazada. MarcarAcreditacionSubidaCommand la rechaza sin versión nueva, y subir la
    /// corregida es lo que la devuelve a Pendiente de subir.
    /// </summary>
    public static bool PuedeSubirVersionCorregida(EstadoAcreditacion estado) => estado == EstadoAcreditacion.Rechazada;

    /// <summary>
    /// «Marcar subido»: solo una Pendiente de subir. Una ya subida espera la respuesta de la plataforma y una Rechazada necesita
    /// antes su versión corregida.
    /// </summary>
    public static bool PuedeMarcarSubido(EstadoAcreditacion estado) => estado == EstadoAcreditacion.PendienteDeSubir;

    /// <summary>«Marcar validado»: lo que la plataforma aún no ha validado (pendiente, subida o rechazada), nunca una ya validada.</summary>
    public static bool PuedeMarcarValidado(EstadoAcreditacion estado) =>
        estado is EstadoAcreditacion.PendienteDeSubir or EstadoAcreditacion.Subida or EstadoAcreditacion.Rechazada;

    /// <summary>«Marcar rechazado…»: las mismas tres que «Marcar validado».</summary>
    public static bool PuedeMarcarRechazado(EstadoAcreditacion estado) => PuedeMarcarValidado(estado);

    /// <summary>
    /// «Anotar vigencia…»: solo una Validada (Aceptada). ConfirmarVigenciaAcreditacionCommand rechaza cualquier otra: lo único que
    /// queda por hacer con una validada es decir hasta cuándo vale en la plataforma.
    /// </summary>
    public static bool PuedeAnotarVigencia(EstadoAcreditacion estado) => estado == EstadoAcreditacion.Aceptada;

    /// <summary>
    /// «Abrir portal»: una Pendiente de subir o una Rechazada, y solo si el canal tiene una dirección http(s) válida (la dirección,
    /// nunca la credencial). Devuelve la dirección normalizada, o <c>null</c> si no se ofrece.
    /// </summary>
    public static string? DireccionDelPortal(EstadoAcreditacion estado, string? urlAccesoCanal) =>
        estado is EstadoAcreditacion.Rechazada or EstadoAcreditacion.PendienteDeSubir ? UrlPortalCae.Normalizar(urlAccesoCanal) : null;

    /// <summary>
    /// Si el menú «Acreditación ▾» de una fila tendría alguna opción (una acreditación «No exigida» de un Vehículo o un Proyecto no
    /// tiene ninguna): sin ella la fila no pinta un menú vacío y conserva «Gestionar», para no quedarse sin acción.
    /// </summary>
    public static bool HayAlgunaAccion(EstadoAcreditacion estado, bool puedeGestionar, string? urlAccesoCanal) =>
        puedeGestionar || PuedeSubirVersionCorregida(estado) || PuedeMarcarSubido(estado) || PuedeMarcarValidado(estado)
        || PuedeMarcarRechazado(estado) || PuedeAnotarVigencia(estado) || DireccionDelPortal(estado, urlAccesoCanal) is not null;
}

namespace CaeManager.Web;

/// <summary>
/// Compartido entre los endpoints que sirven el PDF de un Documento —
/// vigente (<c>DocumentosEndpoints</c>) o una versión anterior desde
/// Auditoría (<c>AuditoriaEndpoints</c>). Antes vivía como método privado
/// solo en <c>DocumentosEndpoints</c>, y la versión anterior se servía sin
/// esta cabecera (Codex, HO-099-01): un reconocimiento médico antiguo podía
/// quedar en la caché de disco del navegador exactamente en el caso que el
/// endpoint vigente ya evita.
/// </summary>
public static class CabecerasArchivoSensible
{
    /// <summary>
    /// Prohíbe almacenar la respuesta en cualquier caché. Sin una directiva
    /// explícita, un navegador puede aplicar caducidad heurística y dejar el
    /// PDF en su caché de disco: un reconocimiento médico —art. 9 RGPD—
    /// sobreviviendo al cierre de sesión en un equipo compartido, que es
    /// justamente lo que servirlo por endpoint autenticado quería evitar.
    ///
    /// Solo se aplica a lo que lleva datos del tenant. No se sube a
    /// <c>UseCabecerasSeguridad</c> porque ahí alcanzaría también a los
    /// estáticos, que sí deben cachearse. <c>X-Content-Type-Options: nosniff</c>
    /// ya lo pone ese middleware para toda la aplicación, esta ruta incluida.
    ///
    /// <c>Pragma</c> es para los intermediarios que solo entienden HTTP/1.0;
    /// es redundante en cualquier cliente actual y no molesta.
    /// </summary>
    public static void ProhibirCache(HttpContext contexto)
    {
        contexto.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        contexto.Response.Headers.Pragma = "no-cache";
    }
}

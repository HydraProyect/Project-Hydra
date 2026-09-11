namespace CaeManager.Application.Reclamaciones;

/// <summary>
/// Lo que <c>EnviarReclamacionCommand</c>/<c>EnviarReclamacionEmpresaCommand</c>
/// REALMENTE enviaron, tras revalidar server-side los DocumentoIds y
/// ContactoIds pedidos (la vista previa puede haberse quedado atrás: un
/// documento se renovó, un contacto salió de la agenda). Ambos handlers
/// siguen la regla "todo o nada" — si algo de lo pedido ya no es válido, el
/// envío entero falla en vez de mandar una parte sin avisar (hallazgo de
/// revisión 2026-09-11: un tercero podía recibir menos de lo anunciado sin
/// que nadie lo supiera) — así que en un envío con éxito esto coincide
/// siempre con lo pedido. Existe para que el contrato lo diga explícitamente
/// en vez de que la UI dé por hecho que "éxito" significa "se envió
/// exactamente lo que anuncié", y para que una futura relajación de la regla
/// "todo o nada" tenga ya el sitio donde declarar la diferencia.
/// </summary>
public sealed record EnvioReclamacionResultado(
    IReadOnlyList<Guid> DocumentoIdsEnviados,
    IReadOnlyList<string> Destinatarios);

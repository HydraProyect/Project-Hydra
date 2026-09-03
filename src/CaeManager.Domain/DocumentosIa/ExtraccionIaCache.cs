using CaeManager.Domain.Common;

namespace CaeManager.Domain.DocumentosIa;

/// <summary>
/// Cache documental (ver docs/ARQUITECTURA-IA-DOCUMENTAL.md § 3): antes de
/// llamar a ningún proveedor, el router comprueba si ya se procesó este mismo
/// archivo <b>con el mismo propósito y con la misma versión del pipeline</b> —
/// si es así, reutiliza el resultado en vez de volver a pagar la extracción.
///
/// <b>Por qué la clave no es solo el hash.</b> Lo fue, con el argumento de que
/// el caso real que se quería evitar era "el mismo archivo físico se sube dos
/// veces", que casi siempre ocurre en el mismo contexto. El "casi" era el
/// problema: lo que se guarda no es una transcripción del archivo, es una
/// <em>interpretación</em> del archivo hecha bajo un tipo esperado concreto —
/// ese tipo entra en el prompt y condiciona qué campos busca el modelo y cómo
/// resuelve las ambigüedades. Con la clave anterior, el mismo PDF procesado
/// primero como "Apto médico" y después como otro tipo devolvía la primera
/// lectura, sin volver a mirar el documento y sin dejar rastro de que la
/// pregunta era otra.
///
/// <b>Y por qué tampoco basta con añadir el tipo.</b> Una entrada de caché
/// sobrevive a los cambios de prompt, de modelo y de esquema de extracción: sin
/// nada que la ate a la versión que la produjo, una corrección del prompt no
/// arregla los documentos ya procesados — el resultado defectuoso queda fijado
/// para ese tenant indefinidamente, y además de forma invisible, porque la
/// auditoría dice "cache" y no qué versión la escribió.
/// <see cref="VersionPipeline"/> lo cierra: subirla invalida de forma
/// determinista todo lo anterior, sin borrar nada y sin migración de datos.
///
/// <see cref="ExtraccionJson"/> guarda en claro los campos extraídos (nombres,
/// DNI, y lo que traiga un documento médico), así que dejarla fuera del ciclo
/// de vida del Documento era el hueco: una purga de retención eliminaba el
/// Documento y su archivo pero no tocaba esta tabla, porque no existía ningún
/// vínculo durable entre una entrada y el Documento del que salió.
///
/// <b>Cerrado por REC-036/DEC-34.</b> <see cref="ExtraccionIaCacheDocumento"/>
/// es ese vínculo — en tabla aparte y no como <c>DocumentoId</c> aquí, porque
/// una misma entrada (indexada por hash) puede corresponder a varios
/// Documentos (el mismo certificado subido para dos trabajadores) y un
/// <c>DocumentoId</c> único obligaría a elegir uno. <c>EjecucionPurgaService</c>
/// borra los vínculos de un Documento al anonimizarlo por retención, y borra
/// con ellos la entrada de esta tabla en cuanto se queda sin ningún vínculo —
/// "sin cachés huérfanas" es literal de DEC-34. Sigue habiendo entradas sin
/// vínculo por diseño (las de mero triage, antes de que exista un Documento:
/// ver el comentario de <see cref="ExtraccionIaCacheDocumento"/>) — esas
/// dependen de una TTL que DEC-34 deja como posibilidad pero sin duración
/// decidida; hallazgo devuelto a la Oficina de Reconciliación en HO-036-01,
/// no implementado aquí.
/// </summary>
public class ExtraccionIaCache : EntidadConTenant
{
    public const int LongitudHash = 64; // SHA256 en hexadecimal
    public const int LongitudMaximaTipoEsperado = 150;
    public const int LongitudMaximaVersionPipeline = 40;

    /// <summary>
    /// Versión del pipeline de extracción que produjo las entradas actuales.
    ///
    /// <b>Súbela en el mismo commit</b> en que cambien los prompts de los
    /// proveedores, el modelo por defecto de alguno, el esquema de
    /// <c>ExtraccionEstructuradaDto</c> o las reglas de post-proceso del
    /// router. Es lo que hace que una corrección alcance también a los
    /// documentos ya procesados en vez de quedarse solo para los nuevos.
    ///
    /// Es una constante y no configuración a propósito: quien cambia el prompt
    /// está tocando este repositorio, y una versión que se pudiera mover desde
    /// fuera permitiría invalidar (o peor, no invalidar) la caché sin dejar
    /// rastro en el historial.
    /// </summary>
    public const string VersionPipelineActual = "2026-08-30";

    public string HashSha256 { get; private set; } = string.Empty;

    /// <summary>El tipo de documento bajo el que se pidió esta lectura, normalizado por <see cref="NormalizarTipoEsperado"/> para que "Apto Médico" y "apto medico " sean la misma clave.</summary>
    public string TipoEsperado { get; private set; } = string.Empty;

    /// <summary>Ver <see cref="VersionPipelineActual"/>. Se guarda el valor vigente al escribir la entrada, no el actual: es lo que permite que una versión nueva deje de encontrar las viejas.</summary>
    public string VersionPipeline { get; private set; } = string.Empty;

    public string ExtraccionJson { get; private set; } = string.Empty;
    public DateTime CreadaEnUtc { get; private set; } = DateTime.UtcNow;

    private ExtraccionIaCache()
    {
    }

    private ExtraccionIaCache(string hashSha256, string tipoEsperado, string versionPipeline, string extraccionJson)
    {
        if (string.IsNullOrWhiteSpace(hashSha256) || hashSha256.Length != LongitudHash)
            throw new ArgumentException($"El hash SHA256 debe tener exactamente {LongitudHash} caracteres.", nameof(hashSha256));
        if (string.IsNullOrWhiteSpace(extraccionJson))
            throw new ArgumentException("El JSON de extracción no puede estar vacío.", nameof(extraccionJson));

        HashSha256 = hashSha256;
        TipoEsperado = tipoEsperado;
        VersionPipeline = versionPipeline;
        ExtraccionJson = extraccionJson;
    }

    public static ExtraccionIaCache Crear(string hashSha256, string tipoEsperado, string extraccionJson) =>
        new(hashSha256, NormalizarTipoEsperado(tipoEsperado), VersionPipelineActual, extraccionJson);

    /// <summary>
    /// Misma normalización al escribir y al leer — si divergieran, la caché
    /// nunca acertaría y el fallo sería invisible: todo seguiría funcionando,
    /// solo que pagando cada extracción dos veces.
    ///
    /// Minúsculas y espacios colapsados, nada más. No se quitan acentos: el
    /// tipo esperado sale del catálogo del tenant, no de la lectura del
    /// modelo, así que llega escrito siempre igual; y una normalización más
    /// agresiva haría colisionar tipos que el catálogo distingue.
    /// </summary>
    public static string NormalizarTipoEsperado(string tipoEsperado)
    {
        var normalizado = string.Join(' ', (tipoEsperado ?? string.Empty)
            .ToLowerInvariant()
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

        return normalizado.Length > LongitudMaximaTipoEsperado
            ? normalizado[..LongitudMaximaTipoEsperado]
            : normalizado;
    }
}

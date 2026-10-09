using System.Text.Json;
using CaeManager.Application.Common;
using CaeManager.Application.Comunicaciones.Deteccion;

namespace CaeManager.Web.Tests.BancoModelosIa;

/// <summary>Métrica: etiqueta correcta (<c>esAccionableCae</c>).</summary>
public sealed record CasoRelevanciaCae(
    string Id, NivelCaso Nivel, string Descripcion, string Conversacion, bool EsAccionableEsperado)
    : CasoBanco(Id, Nivel, Descripcion)
{
    public override RutaIa Ruta => RutaIa.RelevanciaCaeDeConversacion;

    public override async Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken)
    {
        var resultado = await servicios.RelevanciaCae.DetectarAsync(Conversacion, cancellationToken);
        if (resultado.EsFallido)
            return Evaluacion.Fallida(resultado.Error.Codigo);

        return new Evaluacion(
            [new Comprobacion("etiqueta esAccionableCae", resultado.Valor.EsAccionableCae == EsAccionableEsperado,
                $"esperado {EsAccionableEsperado}, obtenido {resultado.Valor.EsAccionableCae}")],
            new Dictionary<string, double> { ["confianza"] = resultado.Valor.Confianza });
    }

    public override string RespuestaIdeal() => Json(EsAccionableEsperado);

    public override string RespuestaErronea() => Json(!EsAccionableEsperado);

    private static string Json(bool etiqueta) =>
        JsonSerializer.Serialize(new { esAccionableCae = etiqueta, resumen = "Resumen simulado.", confianza = 90 });
}

/// <summary>Un ítem esperado de la detección de gestión documental: <c>null</c> significa «el correo no permite resolverlo».</summary>
public sealed record ItemGestionEsperado(Guid? TrabajadorId, Guid? TipoDocumentoId);

/// <summary>
/// Métrica: etiqueta correcta, más aciertos, omisiones e ítems inventados
/// sobre los pares Trabajador / Tipo de documento, más el resumen agregado
/// cuando el correo es de una plataforma que solo da cifras.
/// </summary>
public sealed record CasoGestionCorreo(
    string Id, NivelCaso Nivel, string Descripcion, string Cuerpo,
    IReadOnlyList<TrabajadorCandidatoGestionDto> Trabajadores,
    IReadOnlyList<TipoDocumentoCandidatoGestionDto> TiposDocumento,
    bool EsActualizacionEsperada,
    IReadOnlyList<ItemGestionEsperado> ItemsEsperados,
    ResumenAgregadoGestionDto? ResumenAgregadoEsperado = null)
    : CasoBanco(Id, Nivel, Descripcion)
{
    public override RutaIa Ruta => RutaIa.GestionDocumentalEnCorreo;

    public override async Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken)
    {
        var resultado = await servicios.GestionCorreo.DetectarAsync(Cuerpo, Trabajadores, TiposDocumento, cancellationToken);
        if (resultado.EsFallido)
            return Evaluacion.Fallida(resultado.Error.Codigo);

        var obtenidos = resultado.Valor.Items.Select(i => new ItemGestionEsperado(i.TrabajadorId, i.TipoDocumentoId)).Distinct().ToList();
        var aciertos = ItemsEsperados.Count(obtenidos.Contains);
        var inventados = obtenidos.Count(o => !ItemsEsperados.Contains(o));

        var comprobaciones = new List<Comprobacion>
        {
            new("etiqueta esActualizacionDocumento", resultado.Valor.EsActualizacionDocumento == EsActualizacionEsperada,
                $"esperado {EsActualizacionEsperada}, obtenido {resultado.Valor.EsActualizacionDocumento}"),
        };

        // Una respuesta vacía no inventa nada: «sin inventados» solo puntúa cuando el caso no espera ítems o cuando
        // se ha acertado alguno; si no, callar regalaría el punto. El número de inventados queda siempre en las medidas.
        if (ItemsEsperados.Count == 0 || aciertos > 0 || inventados > 0)
            comprobaciones.Add(new("sin ítems inventados", inventados == 0, $"{inventados} ítems que el correo no pide"));

        // El resumen agregado solo puntúa donde se juega: cuando se espera uno o cuando el modelo da uno que no toca.
        if (ResumenAgregadoEsperado is not null || resultado.Valor.ResumenAgregado is not null)
        {
            comprobaciones.Add(new("resumen agregado", resultado.Valor.ResumenAgregado == ResumenAgregadoEsperado,
                $"esperado {ResumenAgregadoEsperado?.ToString() ?? "ninguno"}, obtenido {resultado.Valor.ResumenAgregado?.ToString() ?? "ninguno"}"));
        }

        comprobaciones.AddRange(ItemsEsperados.Select((item, indice) =>
            new Comprobacion($"ítem esperado {indice + 1}", obtenidos.Contains(item), "omitido o resuelto a otro Trabajador / Tipo de documento")));

        return new Evaluacion(comprobaciones, new Dictionary<string, double>
        {
            ["aciertos"] = aciertos,
            ["omitidos"] = ItemsEsperados.Count - aciertos,
            ["inventados"] = inventados,
        });
    }

    public override string RespuestaIdeal() => Json(EsActualizacionEsperada, ItemsEsperados, ResumenAgregadoEsperado);

    /// <summary>Etiqueta invertida y un único ítem que apunta al último Trabajador y al último Tipo de documento candidatos, que ningún caso espera juntos.</summary>
    public override string RespuestaErronea() =>
        Json(!EsActualizacionEsperada, [new ItemGestionEsperado(Trabajadores[^1].Id, TiposDocumento[^1].Id)], null);

    private static string Json(bool etiqueta, IReadOnlyList<ItemGestionEsperado> items, ResumenAgregadoGestionDto? agregado) =>
        JsonSerializer.Serialize(new
        {
            esActualizacionDocumento = etiqueta,
            resumen = "Resumen simulado.",
            confianza = 90,
            items = items.Select(i => new
            {
                trabajadorId = i.TrabajadorId?.ToString(),
                tipoDocumentoId = i.TipoDocumentoId?.ToString(),
                confianzaTrabajador = i.TrabajadorId is null ? 0 : 90,
                confianzaTipoDocumento = i.TipoDocumentoId is null ? 0 : 90,
            }),
            resumenAgregado = agregado is null
                ? null
                : new { pendientes = agregado.Pendientes, vencidos = agregado.Vencidos, rechazados = agregado.Rechazados },
        });
}

/// <summary>
/// Métrica: etiqueta, Centro y las dos fechas, un punto cada una. En una
/// Visita de un solo día el prompt no fija si <c>fechaFin</c> repite la
/// fecha o queda vacía: se aceptan las dos formas.
/// </summary>
public sealed record CasoVisitaCorreo(
    string Id, NivelCaso Nivel, string Descripcion, string Cuerpo,
    IReadOnlyList<CentroCandidatoVisitaDto> Centros, DateOnly FechaReferencia,
    bool EsSolicitudEsperada, Guid? CentroEsperado, DateOnly? FechaInicioEsperada, DateOnly? FechaFinEsperada)
    : CasoBanco(Id, Nivel, Descripcion)
{
    public override RutaIa Ruta => RutaIa.VisitaEnCorreo;

    private bool EsDeUnSoloDia => FechaInicioEsperada is not null && FechaInicioEsperada == FechaFinEsperada;

    public override async Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken)
    {
        var resultado = await servicios.VisitaCorreo.DetectarAsync(Cuerpo, Centros, FechaReferencia, cancellationToken);
        if (resultado.EsFallido)
            return Evaluacion.Fallida(resultado.Error.Codigo);

        var v = resultado.Valor;
        var finCorrecto = v.FechaFin == FechaFinEsperada || (EsDeUnSoloDia && v.FechaFin is null);

        return new Evaluacion(
            [
                new Comprobacion("etiqueta esSolicitudVisita", v.EsSolicitudVisita == EsSolicitudEsperada,
                    $"esperado {EsSolicitudEsperada}, obtenido {v.EsSolicitudVisita}"),
                new Comprobacion("Centro", v.CentroId == CentroEsperado, $"esperado {Nombre(CentroEsperado)}, obtenido {Nombre(v.CentroId)}"),
                new Comprobacion("fecha de inicio", v.FechaInicio == FechaInicioEsperada, $"esperado {FechaInicioEsperada:O}, obtenido {v.FechaInicio:O}"),
                new Comprobacion("fecha de fin", finCorrecto, $"esperado {FechaFinEsperada:O}, obtenido {v.FechaFin:O}"),
            ],
            new Dictionary<string, double> { ["confianza"] = v.Confianza });
    }

    private string Nombre(Guid? centroId) =>
        centroId is null ? "ninguno" : Centros.FirstOrDefault(c => c.Id == centroId)?.Nombre ?? centroId.Value.ToString();

    public override string RespuestaIdeal() => Json(EsSolicitudEsperada, CentroEsperado, FechaInicioEsperada, FechaFinEsperada);

    /// <summary>Etiqueta invertida, otro Centro de la lista y las fechas corridas un día.</summary>
    public override string RespuestaErronea() =>
        Json(
            !EsSolicitudEsperada,
            Centros.Select(c => (Guid?)c.Id).First(id => id != CentroEsperado),
            (FechaInicioEsperada ?? FechaReferencia).AddDays(1),
            (FechaFinEsperada ?? FechaReferencia).AddDays(2));

    private static string Json(bool etiqueta, Guid? centro, DateOnly? inicio, DateOnly? fin) =>
        JsonSerializer.Serialize(new
        {
            esSolicitudVisita = etiqueta,
            centroId = centro?.ToString(),
            fechaInicio = inicio?.ToString("yyyy-MM-dd"),
            fechaFin = fin?.ToString("yyyy-MM-dd"),
            resumen = "Resumen simulado.",
            confianza = 90,
            confianzaCentro = centro is null ? 0 : 90,
            confianzaFechas = inicio is null ? 0 : 90,
        });
}

/// <summary>
/// Métrica: recuerdo de palabras de la transcripción de referencia (umbral
/// 0,95), fragmentos críticos que deben aparecer tal cual (identificadores,
/// fechas) y fragmentos que no deben aparecer (texto que el documento no
/// contiene, o la obediencia a una instrucción incrustada).
/// </summary>
public sealed record CasoTranscripcion(
    string Id, NivelCaso Nivel, string Descripcion, byte[] Archivo, string NombreArchivo,
    string TextoDeReferencia, IReadOnlyList<string> FragmentosCriticos, IReadOnlyList<string> FragmentosProhibidos)
    : CasoBanco(Id, Nivel, Descripcion)
{
    public const double RecuerdoMinimo = 0.95;

    public override RutaIa Ruta => RutaIa.TranscripcionDeDocumento;

    public override async Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken)
    {
        var resultado = await servicios.Documental.ExtraerTextoAsync(Archivo, NombreArchivo, cancellationToken);
        if (resultado.EsFallido)
            return Evaluacion.Fallida(resultado.Error.Codigo);

        var transcrito = resultado.Valor.Texto;
        var recuerdo = Recuerdo(transcrito);

        var comprobaciones = new List<Comprobacion>
        {
            new($"recuerdo de palabras ≥ {RecuerdoMinimo:0.00}", recuerdo >= RecuerdoMinimo, $"recuerdo {recuerdo:0.000}"),
        };
        comprobaciones.AddRange(FragmentosCriticos.Select(f => new Comprobacion($"contiene «{f}»", TextoBanco.Contiene(transcrito, f))));
        comprobaciones.AddRange(FragmentosProhibidos.Select(f => new Comprobacion($"no contiene «{f}»", !TextoBanco.Contiene(transcrito, f))));

        return new Evaluacion(comprobaciones, new Dictionary<string, double> { ["recuerdoPalabras"] = Math.Round(recuerdo, 4) });
    }

    /// <summary>Proporción de palabras de la referencia presentes en la transcripción, contando repeticiones.</summary>
    private double Recuerdo(string transcrito)
    {
        var disponibles = TextoBanco.Palabras(transcrito)
            .GroupBy(TextoBanco.Identificador)
            .ToDictionary(g => g.Key, g => g.Count());

        var referencia = TextoBanco.Palabras(TextoDeReferencia).Select(TextoBanco.Identificador).Where(p => p.Length > 0).ToList();
        var encontradas = 0;
        foreach (var palabra in referencia)
        {
            if (disponibles.TryGetValue(palabra, out var quedan) && quedan > 0)
            {
                disponibles[palabra] = quedan - 1;
                encontradas++;
            }
        }

        return referencia.Count == 0 ? 0 : (double)encontradas / referencia.Count;
    }

    public override string RespuestaIdeal() => TextoDeReferencia;

    public override string RespuestaErronea() =>
        "Certificado de ejemplo sin relación con el documento. " + string.Join(' ', FragmentosProhibidos);
}

/// <summary>
/// Métrica: exactitud por campo. Los identificadores se comparan sin
/// separadores; los campos de texto libre, por contenido; y los campos de
/// <paramref name="CamposQueDebenFaltar"/> cuentan como error si el modelo
/// los rellena, porque el documento no los trae.
/// </summary>
public sealed record CasoExtraccionEstructurada(
    string Id, NivelCaso Nivel, string Descripcion, string TipoEsperado, string Texto,
    IReadOnlyDictionary<string, string> CamposExactos,
    IReadOnlyDictionary<string, string> CamposQueDebenContener,
    IReadOnlyList<string> CamposQueDebenFaltar)
    : CasoBanco(Id, Nivel, Descripcion)
{
    private static readonly HashSet<string> CamposIdentificador = ["cifEmpresa", "documentoIdentidadTrabajador"];

    public override RutaIa Ruta => RutaIa.ExtraccionEstructurada;

    public override async Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken)
    {
        var resultado = await servicios.Documental.ExtraerEstructuradoAsync(Texto, TipoEsperado, cancellationToken);
        if (resultado.EsFallido)
            return Evaluacion.Fallida(resultado.Error.Codigo);

        var campos = resultado.Valor.Campos;
        var comprobaciones = new List<Comprobacion>();

        foreach (var (campo, esperado) in CamposExactos)
        {
            var obtenido = campos.GetValueOrDefault(campo);
            var igual = CamposIdentificador.Contains(campo)
                ? TextoBanco.Identificador(obtenido) == TextoBanco.Identificador(esperado)
                : string.Equals(obtenido?.Trim(), esperado, StringComparison.OrdinalIgnoreCase);
            comprobaciones.Add(new Comprobacion($"campo {campo}", igual, $"esperado «{esperado}», obtenido «{obtenido ?? "ausente"}»"));
        }

        foreach (var (campo, fragmento) in CamposQueDebenContener)
        {
            var obtenido = campos.GetValueOrDefault(campo);
            comprobaciones.Add(new Comprobacion(
                $"campo {campo}", TextoBanco.Contiene(obtenido, fragmento), $"debía mencionar «{fragmento}», obtenido «{obtenido ?? "ausente"}»"));
        }

        foreach (var campo in CamposQueDebenFaltar)
        {
            var obtenido = campos.GetValueOrDefault(campo);
            comprobaciones.Add(new Comprobacion(
                $"campo {campo} sin inventar", string.IsNullOrWhiteSpace(obtenido), $"el documento no lo trae, obtenido «{obtenido}»"));
        }

        return new Evaluacion(comprobaciones, new Dictionary<string, double> { ["confianzaGeneral"] = resultado.Valor.ConfianzaGeneral });
    }

    public override string RespuestaIdeal() =>
        Json(CamposExactos.Concat(CamposQueDebenContener).ToDictionary(c => c.Key, c => c.Value));

    /// <summary>Todos los campos esperados con un valor distinto, y los que debían faltar, rellenos.</summary>
    public override string RespuestaErronea() =>
        Json(CamposExactos.Keys.Concat(CamposQueDebenContener.Keys).Concat(CamposQueDebenFaltar).ToDictionary(c => c, _ => "2099-12-31"));

    private string Json(Dictionary<string, string> campos) =>
        JsonSerializer.Serialize(new { tipoDetectado = TipoEsperado, campos, confianzaGeneral = 90, notasValidacion = (string?)null });
}

/// <summary>Un Trabajador que el listado sintético contiene y que la ruta debe devolver.</summary>
public sealed record TrabajadorEsperado(string Nombre, string Apellidos, string Dni);

/// <summary>
/// Métrica: aciertos, omisiones e inventados por DNI, y nombre correcto en
/// los aciertos. <paramref name="Dudosos"/> son los DNI que el documento
/// marca como baja: el prompt pide «trabajadores dados de alta» pero no dice
/// qué hacer con una baja listada, así que ni suman ni restan y se informan
/// aparte (medida <c>bajasIncluidas</c>).
/// </summary>
public sealed record CasoListadoTrabajadores(
    string Id, NivelCaso Nivel, string Descripcion, byte[] Pdf,
    IReadOnlyList<TrabajadorEsperado> Esperados, IReadOnlyList<string> Dudosos)
    : CasoBanco(Id, Nivel, Descripcion)
{
    public override RutaIa Ruta => RutaIa.ListadoDeTrabajadores;

    public override async Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken)
    {
        var resultado = await servicios.ListadoTrabajadores.ExtraerAsync(Pdf, cancellationToken);
        if (resultado.EsFallido)
            return Evaluacion.Fallida(resultado.Error.Codigo);

        var obtenidos = resultado.Valor
            .GroupBy(t => TextoBanco.Identificador(t.Dni))
            .ToDictionary(g => g.Key, g => g.First());
        var dudosos = Dudosos.Select(TextoBanco.Identificador).ToHashSet();
        var esperados = Esperados.ToDictionary(e => TextoBanco.Identificador(e.Dni));

        var aciertos = esperados.Keys.Count(obtenidos.ContainsKey);
        var inventados = obtenidos.Keys.Count(dni => !esperados.ContainsKey(dni) && !dudosos.Contains(dni));
        var nombresMal = esperados
            .Where(e => obtenidos.ContainsKey(e.Key))
            .Count(e => !NombreCoincide(e.Value, obtenidos[e.Key].Nombre, obtenidos[e.Key].Apellidos));

        return new Evaluacion(
            [
                new Comprobacion("sin omisiones", aciertos == esperados.Count, $"{esperados.Count - aciertos} de {esperados.Count} Trabajadores omitidos"),
                new Comprobacion("sin Trabajadores inventados", inventados == 0, $"{inventados} DNI que el documento no lista como legibles"),
                new Comprobacion("nombre y apellidos de cada acierto", nombresMal == 0, $"{nombresMal} aciertos con el nombre o los apellidos cambiados"),
            ],
            new Dictionary<string, double>
            {
                ["esperados"] = esperados.Count,
                ["aciertos"] = aciertos,
                ["omitidos"] = esperados.Count - aciertos,
                ["inventados"] = inventados,
                ["bajasIncluidas"] = obtenidos.Keys.Count(dudosos.Contains),
            });
    }

    /// <summary>El documento da «APELLIDOS, NOMBRE»: se acepta cualquier reparto entre los dos campos mientras estén todas las palabras.</summary>
    private static bool NombreCoincide(TrabajadorEsperado esperado, string nombre, string apellidos)
    {
        var obtenido = $"{nombre} {apellidos}";
        return TextoBanco.Palabras($"{esperado.Nombre} {esperado.Apellidos}").All(palabra => TextoBanco.Contiene(obtenido, palabra));
    }

    public override string RespuestaIdeal() => Json(Esperados);

    /// <summary>Omite al primero (si lo hay), cambia el nombre del resto y añade un Trabajador que el documento no contiene.</summary>
    public override string RespuestaErronea() =>
        Json([.. Esperados.Skip(1).Select(e => e with { Nombre = "Otro" }), new TrabajadorEsperado("Inventado", "Sin Documento", "99999999X")]);

    private static string Json(IEnumerable<TrabajadorEsperado> trabajadores) =>
        JsonSerializer.Serialize(trabajadores.Select(t => new { nombre = t.Nombre, apellidos = t.Apellidos, dni = t.Dni }));
}

/// <summary>
/// Métrica: criterios comprobables sobre la respuesta, nunca «parece bien».
/// Cada grupo de <paramref name="DebeContenerAlguno"/> se cumple con que
/// aparezca una de sus formulaciones.
/// </summary>
public sealed record CasoChat(
    string Id, NivelCaso Nivel, string Descripcion, IReadOnlyList<MensajeChatDto> Historial,
    IReadOnlyList<string> DebeContener,
    IReadOnlyList<IReadOnlyList<string>> DebeContenerAlguno,
    IReadOnlyList<string> NoDebeContener)
    : CasoBanco(Id, Nivel, Descripcion)
{
    public const string FraseFueraDeAmbito = "Mi ámbito de actuación se limita a Coordinación de Actividades Empresariales y Prevención de Riesgos Laborales";

    public static readonly IReadOnlyList<string> SeccionesDelFormato = ["## Respuesta", "## Base legal", "## Recomendación para Gestor CAE"];

    public override RutaIa Ruta => RutaIa.ChatDelAsistente;

    public override async Task<Evaluacion> EjecutarAsync(ServiciosAnthropic servicios, CancellationToken cancellationToken)
    {
        var resultado = await servicios.Chat.PreguntarAsync(Historial, cancellationToken);
        if (resultado.EsFallido)
            return Evaluacion.Fallida(resultado.Error.Codigo);

        var respuesta = resultado.Valor;
        var comprobaciones = new List<Comprobacion>();
        comprobaciones.AddRange(DebeContener.Select(f => new Comprobacion($"contiene «{f}»", TextoBanco.Contiene(respuesta, f))));
        comprobaciones.AddRange(DebeContenerAlguno.Select(grupo =>
            new Comprobacion($"contiene alguna de: {string.Join(" | ", grupo)}", grupo.Any(f => TextoBanco.Contiene(respuesta, f)))));
        comprobaciones.AddRange(NoDebeContener.Select(f => new Comprobacion($"no contiene «{f}»", !TextoBanco.Contiene(respuesta, f))));

        return new Evaluacion(comprobaciones, new Dictionary<string, double> { ["caracteres"] = respuesta.Length });
    }

    public override string RespuestaIdeal() =>
        string.Join("\n\n", DebeContener.Concat(DebeContenerAlguno.Select(grupo => grupo[0])));

    public override string RespuestaErronea() =>
        "Claro, aquí tienes. " + string.Join(' ', NoDebeContener);
}
